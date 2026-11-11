using MealRoute.Core.Benchmark;
using MealRoute.Core.Editing;
using MealRoute.Core.Geo;
using MealRoute.Core.GeoJson;
using MealRoute.Core.Model;
using MealRoute.Core.Network;
using MealRoute.Core.Optimization;
using MealRoute.Core.Planning;
using MealRoute.Core.Projects;
using MealRoute.Core.Rescheduling;
using MealRoute.Core.Travel;

namespace MealRoute.App.ViewModels;

public enum WarningKind { NetworkImport, SmallComponent, Snapping, Project }

public sealed record WorkspaceWarning(WarningKind Kind, string Message, int? StopIndex = null);

/// <summary>
/// Everything loaded for one planning day: project data, road network, travel model and the
/// interactive editor. In static mode (Solomon) there is no network; travel is Euclidean.
/// </summary>
public sealed class PlanningWorkspace
{
    private PlanningWorkspace(ProjectData project, string? projectPath)
    {
        Project = project;
        ProjectPath = projectPath;
        Instance = project.ToInstance();
    }

    public ProjectData Project { get; private set; }
    public string? ProjectPath { get; private set; }
    public ProblemInstance Instance { get; private set; }
    public RoadNetwork? Network { get; private set; }
    public NetworkAnalysis? Analysis { get; private set; }
    public TravelModel? Model { get; private set; }
    public ITravelTimeOracle? SearchOracle { get; private set; }
    public ITravelTimeOracle? ExactOracle { get; private set; }
    public PlanEditor? Editor { get; private set; }
    public bool IsStaticMode { get; private set; }
    public List<WorkspaceWarning> Warnings { get; } = new();

    public static PlanningWorkspace Empty() => new(new ProjectData("新專案", null, SpeedProfile.Default, BreakRule.Default,
        Array.Empty<Stop>(), Array.Empty<Vehicle>(), null, LockSet.Empty, new OptimizerSettings()), null);

    /// <summary>Opens a project: reads the network (warns if its hash changed) and builds the travel model.</summary>
    public static async Task<PlanningWorkspace> OpenAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var project = await Task.Run(() => ProjectSerializer.Load(path), ct).ConfigureAwait(false);
        var ws = new PlanningWorkspace(project, path);
        if (project.Network is { } net)
        {
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var netPath = ws.ResolveNetworkPath(net.Path);
            // Allowlist on the exact path that will be opened.
            if (!IsAllowedReferencedPath(netPath, projectDir))
                throw new ProjectFileException($"專案參照的路網路徑「{net.Path}」是網路或裝置路徑，基於安全不會自動開啟。請確認來源後，以「匯入路網」手動選擇檔案。");
            if (!File.Exists(netPath)) throw new ProjectFileException($"找不到專案參照的路網檔：{netPath}");
            if (!string.Equals(ProjectSerializer.Sha256Of(netPath), net.Sha256, StringComparison.OrdinalIgnoreCase))
                ws.Warnings.Add(new WorkspaceWarning(WarningKind.Project, "路網檔內容與專案記錄的不同（檔案已被修改），結果可能與存檔時不一致。"));
            await ws.LoadNetworkFileAsync(netPath, rebuild: false, progress, ct).ConfigureAwait(false);
        }
        await ws.RebuildAsync(progress, ct).ConfigureAwait(false);
        return ws;
    }

    /// <summary>Opens a Solomon instance in static (Euclidean, no network) mode.</summary>
    public static PlanningWorkspace OpenSolomon(string path)
    {
        var sol = SolomonInstance.Load(path);
        var inst = sol.ToProblemInstance();
        var ws = new PlanningWorkspace(new ProjectData(sol.Name, null, SpeedProfile.Default, BreakRule.None,
            inst.Stops, inst.Vehicles, null, LockSet.Empty, new OptimizerSettings()), null) { IsStaticMode = true };
        var oracle = SolomonInstance.Oracle(ws.Instance);
        ws.SearchOracle = ws.ExactOracle = oracle;
        ws.Editor = new PlanEditor(ws.Instance, oracle, oracle, EmptySolution(ws.Instance));
        return ws;
    }

    public async Task LoadNetworkFileAsync(string path, bool rebuild, IProgress<double>? progress, CancellationToken ct)
    {
        var (read, net, analysis) = await Task.Run(() =>
        {
            var r = GeoJsonRoadReader.ReadFile(path);
            var n = NetworkBuilder.Build(r.Features);
            return (r, n, NetworkAnalysis.Analyze(n));
        }, ct).ConfigureAwait(false);
        Network = net;
        Analysis = analysis;
        Warnings.RemoveAll(w => w.Kind is WarningKind.NetworkImport or WarningKind.SmallComponent);
        Warnings.AddRange(read.Issues.Select(i => new WorkspaceWarning(WarningKind.NetworkImport, i.ToString())));
        Warnings.AddRange(analysis.SmallComponents.Select(c => new WorkspaceWarning(WarningKind.SmallComponent,
            $"路網小分量（{c.NodeCount} 個節點，與主路網不互通）：{string.Join("、", c.FeatureIds.Take(5))}{(c.FeatureIds.Count > 5 ? "…" : "")}")));
        if (Project.Network is null || !SamePath(ResolveNetworkPath(Project.Network.Path), path))
            Project = Project with { Network = new NetworkReference(RelativeToProject(path), ProjectSerializer.Sha256Of(path)) };
        if (rebuild) await RebuildAsync(progress, ct).ConfigureAwait(false);
    }

    public async Task SetRosterAsync(IReadOnlyList<Stop>? stops, IReadOnlyList<Vehicle>? vehicles, IProgress<double>? progress, CancellationToken ct)
    {
        Project = Project with
        {
            Stops = stops ?? Project.Stops,
            Vehicles = vehicles ?? Project.Vehicles,
            Solution = null, // a different roster invalidates the plan
            Locks = LockSet.Empty,
        };
        Instance = Project.ToInstance();
        await RebuildAsync(progress, ct).ConfigureAwait(false);
    }

    /// <summary>Snaps all locations and builds L1/L2; restores the stored solution if it still fits.</summary>
    public async Task RebuildAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Instance = Project.ToInstance();
        Warnings.RemoveAll(w => w.Kind == WarningKind.Snapping);
        if (Network is null || Analysis is null || Instance.Vehicles.Count == 0)
        {
            Editor = null;
            Model = null;
            return;
        }
        var (h0, h1) = TravelModel.Horizon(Instance);
        var model = await Task.Run(() => TravelModel.Build(Network, Analysis, Project.SpeedProfile, Instance.LocationPoints(), h0, h1,
            new OracleBuildOptions { Progress = progress, CancellationToken = ct }), ct).ConfigureAwait(false);
        Model = model;
        SearchOracle = model.Search;
        ExactOracle = model.Exact;
        var S = Instance.Stops.Count;
        foreach (var w in model.Snapped.Warnings)
        {
            var i = TravelModel.LocationOf(w);
            Warnings.Add(new WorkspaceWarning(WarningKind.Snapping, $"{Instance.DescribeLocation(i)}：{w.Message}", i < S ? i : null));
        }
        var solution = Project.Solution is { } sol && sol.Routes.Count == Instance.Vehicles.Count ? sol : EmptySolution(Instance);
        Editor = new PlanEditor(Instance, model.Search, model.Exact, solution);
        foreach (var v in Project.Locks.LockedVehicles) Editor.LockRoute(v);
        foreach (var (stop, (v, pos)) in Project.Locks.LockedStops)
            if (pos < solution.Routes[v].Stops.Count && solution.Routes[v].Stops[pos] == stop) Editor.LockStop(stop);
    }

    public Optimizer CreateOptimizer() =>
        new(Instance, SearchOracle ?? throw new InvalidOperationException("尚未建立行車時間模型。"), ExactOracle!);

    public Rescheduler CreateRescheduler() => new(Instance, (points, ct) =>
    {
        if (IsStaticMode || Network is null)
        {
            var o = new EuclideanOracle(points.Select(p => (p.Lon, p.Lat)).ToArray());
            return (o, o);
        }
        var (h0, h1) = TravelModel.Horizon(Instance);
        var m = TravelModel.Build(Network, Analysis!, Project.SpeedProfile, points, h0, h1, new OracleBuildOptions { CancellationToken = ct });
        return (m.Search, m.Exact);
    });

    /// <summary>Path of a route for drawing (driven path with a network, straight lines in static mode).</summary>
    public IReadOnlyList<GeoPoint> RouteGeometry(RoutePlan route) =>
        Model is { } m && !IsStaticMode
            ? m.RouteGeometry(Instance, route)
            : new[] { Instance.Vehicles[route.Vehicle].StartDepot }.Concat(route.Stops.Select(s => Instance.Stops[s].Location)).Append(Instance.Vehicles[route.Vehicle].EndDepot).ToArray();

    public void Save(string path)
    {
        // Resolve the network against the old location, then re-express it relative to the new one.
        var networkAbsolute = Project.Network is { } net ? ResolveNetworkPath(net.Path) : null;
        ProjectPath = Path.GetFullPath(path);
        if (networkAbsolute is not null)
            Project = Project with { Network = Project.Network! with { Path = RelativeToProject(networkAbsolute) } };
        Project = Project with
        {
            Solution = Editor?.ToSolution() ?? Project.Solution,
            Locks = Editor?.Locks ?? Project.Locks,
        };
        ProjectSerializer.Save(Project, ProjectPath);
    }

    public void UpdateOptimizerSettings(OptimizerSettings settings) => Project = Project with { Optimizer = settings };

    public static Solution EmptySolution(ProblemInstance inst) => new(
        Enumerable.Range(0, inst.Vehicles.Count).Select(RoutePlan.Empty).ToArray(),
        Enumerable.Range(0, inst.Stops.Count).Select(s => new UnservedStop(s, "尚未排班")).ToArray());

    private string ResolveNetworkPath(string path, string? baseDir = null)
    {
        if (Path.IsPathRooted(path)) return path;
        var dir = baseDir ?? (ProjectPath is null ? Environment.CurrentDirectory : Path.GetDirectoryName(ProjectPath)!);
        return Path.GetFullPath(Path.Combine(dir, path));
    }

    private string RelativeToProject(string absolute) =>
        ProjectPath is null ? Path.GetFullPath(absolute) : Path.GetRelativePath(Path.GetDirectoryName(ProjectPath)!, absolute);

    /// <summary>
    /// A file path that comes from a (possibly shared) project file is only followed when, fully
    /// resolved, it is on a local drive letter (X:\, not a mapped network drive) or inside the
    /// project's own folder. Everything else — UNC shares, \\?\, \\.\, \??\ device paths — would
    /// make Windows contact another host and possibly send the user's credentials.
    /// </summary>
    public static bool IsAllowedReferencedPath(string fullPath, string projectDirectory)
    {
        if (IsInside(fullPath, projectDirectory)) return true;
        var root = Path.GetPathRoot(fullPath);
        if (root is null || root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || root[2] != '\\') return false;
        try { return new DriveInfo(root).DriveType is not (DriveType.Network or DriveType.NoRootDirectory); }
        catch (ArgumentException) { return false; }
    }

    private static bool IsInside(string fullPath, string directory)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(fullPath).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
