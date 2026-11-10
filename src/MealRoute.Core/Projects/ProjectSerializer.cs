using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MealRoute.Core.Geo;
using MealRoute.Core.Model;
using MealRoute.Core.Optimization;
using MealRoute.Core.Travel;

namespace MealRoute.Core.Projects;

/// <summary>
/// Project file (JSON). Stops and vehicles are referenced by id, numbers are written with
/// round-trip precision (infinities as "Infinity"), so save → load is lossless.
/// </summary>
public static class ProjectSerializer
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ------------------------------------------------------------------ DTOs (file format v1)

    private sealed class FileDto
    {
        public int SchemaVersion { get; set; }
        public string Name { get; set; } = "";
        public NetworkDto? Network { get; set; }
        public SpeedDto? SpeedProfile { get; set; }
        public BreakDto? BreakRule { get; set; }
        public List<StopDto>? Stops { get; set; }
        public List<VehicleDto>? Vehicles { get; set; }
        public SolutionDto? Solution { get; set; }
        public LocksDto? Locks { get; set; }
        public OptimizerSettings? Optimizer { get; set; }
    }

    private sealed record NetworkDto(string Path, string Sha256);
    private sealed record SpeedDto(double[] Arterial, double[] Collector, double[] Residential);
    private sealed record BreakDto(double MaxDrivingSeconds, double BreakSeconds);
    private sealed record StopDto(string Id, string Name, double Lat, double Lon, string Address, string Note, int Meals, int Boxes,
        double ServiceSeconds, double WindowStart, double WindowEnd, bool Mandatory, int Priority);
    private sealed record VehicleDto(string Id, string DriverName, double StartLat, double StartLon, double EndLat, double EndLon,
        double EarliestStart, double LatestEnd, int MealCapacity, int BoxCapacity, double MaxWorkSeconds);
    private sealed record VisitDto(string Stop, double Arrival, double ServiceStart, double Departure, bool BreakAfter);
    private sealed record RouteDto(string Vehicle, double Departure, double EndArrival, List<VisitDto> Visits);
    private sealed record UnservedDto(string Stop, string Reason);
    private sealed record SolutionDto(List<RouteDto> Routes, List<UnservedDto> Unserved);
    private sealed record StopLockDto(string Stop, string Vehicle, int Position);
    private sealed record LocksDto(List<string> Routes, List<StopLockDto> Stops);

    // ------------------------------------------------------------------ save

    public static void Save(ProjectData project, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(project), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true); // never leave a half-written project behind
    }

    public static string Serialize(ProjectData p)
    {
        var stopId = p.Stops.Select(s => s.Id).ToArray();
        var vehId = p.Vehicles.Select(v => v.Id).ToArray();
        var dto = new FileDto
        {
            SchemaVersion = CurrentVersion,
            Name = p.Name,
            Network = p.Network is null ? null : new NetworkDto(p.Network.Path, p.Network.Sha256),
            SpeedProfile = new SpeedDto(p.SpeedProfile.SpeedsKmh(Network.RoadClass.Arterial), p.SpeedProfile.SpeedsKmh(Network.RoadClass.Collector), p.SpeedProfile.SpeedsKmh(Network.RoadClass.Residential)),
            BreakRule = new BreakDto(p.Breaks.MaxDrivingSeconds, p.Breaks.BreakSeconds),
            Stops = p.Stops.Select(s => new StopDto(s.Id, s.Name, s.Location.Lat, s.Location.Lon, s.Address, s.Note, s.Meals, s.Boxes,
                s.ServiceSeconds, s.WindowStart, s.WindowEnd, s.Mandatory, s.Priority)).ToList(),
            Vehicles = p.Vehicles.Select(v => new VehicleDto(v.Id, v.DriverName, v.StartDepot.Lat, v.StartDepot.Lon, v.EndDepot.Lat, v.EndDepot.Lon,
                v.EarliestStart, v.LatestEnd, v.MealCapacity, v.BoxCapacity, v.MaxWorkSeconds)).ToList(),
            Solution = p.Solution is null ? null : new SolutionDto(
                p.Solution.Routes.Where(r => !r.IsEmpty).Select(r => new RouteDto(vehId[r.Vehicle], r.Departure, r.EndArrival,
                    r.Visits.Select(x => new VisitDto(stopId[x.Stop], x.Arrival, x.ServiceStart, x.Departure, x.BreakAfter)).ToList())).ToList(),
                p.Solution.Unserved.Select(u => new UnservedDto(stopId[u.Stop], u.Reason)).ToList()),
            Locks = new LocksDto(
                p.Locks.LockedVehicles.OrderBy(v => v).Select(v => vehId[v]).ToList(),
                p.Locks.LockedStops.OrderBy(x => x.Key).Select(x => new StopLockDto(stopId[x.Key], vehId[x.Value.Vehicle], x.Value.Position)).ToList()),
            Optimizer = p.Optimizer,
        };
        return JsonSerializer.Serialize(dto, Json);
    }

    // ------------------------------------------------------------------ load

    public static ProjectData Load(string path)
    {
        if (!File.Exists(path)) throw new ProjectFileException($"找不到專案檔：{path}");
        string text;
        try { text = File.ReadAllText(path, Encoding.UTF8); }
        catch (IOException ex) { throw new ProjectFileException($"無法讀取專案檔：{ex.Message}", ex); }
        return Deserialize(text);
    }

    public static ProjectData Deserialize(string text)
    {
        // Version check first so newer files give the right message even if their shape differs.
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("schemaVersion", out var ver) || !ver.TryGetInt32(out var version))
                throw new ProjectFileException("這不是 MealRoute 專案檔（缺少 schemaVersion）。");
            if (version > CurrentVersion)
                throw new ProjectFileException($"此專案由較新版本的 MealRoute 建立（格式版本 {version}），目前程式僅支援到版本 {CurrentVersion}，請更新程式後再開啟。");
            if (version < 1)
                throw new ProjectFileException($"不支援的專案格式版本 {version}。");
        }
        catch (JsonException ex)
        {
            throw new ProjectFileException($"專案檔已損毀：第 {(ex.LineNumber ?? 0) + 1} 行附近無法解析 JSON。", ex);
        }

        FileDto dto;
        try { dto = JsonSerializer.Deserialize<FileDto>(text, Json) ?? throw new ProjectFileException("專案檔內容是空的。"); }
        catch (JsonException ex) { throw new ProjectFileException($"專案檔格式錯誤：{ex.Message}", ex); }

        var stopsDto = dto.Stops ?? throw new ProjectFileException("專案檔缺少 stops。");
        var vehiclesDto = dto.Vehicles ?? throw new ProjectFileException("專案檔缺少 vehicles。");
        var stops = stopsDto.Select(s => new Stop(s.Id, s.Name, new GeoPoint(s.Lat, s.Lon), s.Address ?? "", s.Note ?? "", s.Meals, s.Boxes,
            s.ServiceSeconds, s.WindowStart, s.WindowEnd, s.Mandatory, s.Priority)).ToArray();
        var vehicles = vehiclesDto.Select(v => new Vehicle(v.Id, v.DriverName, new GeoPoint(v.StartLat, v.StartLon), new GeoPoint(v.EndLat, v.EndLon),
            v.EarliestStart, v.LatestEnd, v.MealCapacity, v.BoxCapacity, v.MaxWorkSeconds)).ToArray();

        var stopIndex = Index(stops.Select(s => s.Id), "站點");
        var vehIndex = Index(vehicles.Select(v => v.Id), "車輛");
        int Stop(string id) => stopIndex.TryGetValue(id, out var i) ? i : throw new ProjectFileException($"專案檔參照了不存在的站點 id「{id}」。");
        int Veh(string id) => vehIndex.TryGetValue(id, out var i) ? i : throw new ProjectFileException($"專案檔參照了不存在的車輛 id「{id}」。");

        SpeedProfile profile;
        try
        {
            profile = dto.SpeedProfile is null ? SpeedProfile.Default : new SpeedProfile(dto.SpeedProfile.Arterial, dto.SpeedProfile.Collector, dto.SpeedProfile.Residential);
        }
        catch (ArgumentException ex) { throw new ProjectFileException($"速度表無效：{ex.Message}", ex); }

        Solution? solution = null;
        if (dto.Solution is { } sd)
        {
            var routes = Enumerable.Range(0, vehicles.Length).Select(RoutePlan.Empty).ToArray();
            foreach (var r in sd.Routes ?? new())
            {
                var v = Veh(r.Vehicle);
                var visits = (r.Visits ?? new()).Select(x => new StopVisit(Stop(x.Stop), x.Arrival, x.ServiceStart, x.Departure, x.BreakAfter)).ToArray();
                routes[v] = new RoutePlan(v, visits.Select(x => x.Stop).ToArray(), r.Departure, visits, r.EndArrival);
            }
            solution = new Solution(routes, (sd.Unserved ?? new()).Select(u => new UnservedStop(Stop(u.Stop), u.Reason ?? "")).ToArray());
        }

        var locks = new LockSet(
            new HashSet<int>((dto.Locks?.Routes ?? new()).Select(Veh)),
            (dto.Locks?.Stops ?? new()).ToDictionary(l => Stop(l.Stop), l => (Veh(l.Vehicle), l.Position)));

        return new ProjectData(dto.Name ?? "", dto.Network is null ? null : new NetworkReference(dto.Network.Path, dto.Network.Sha256),
            profile, dto.BreakRule is null ? BreakRule.Default : new BreakRule(dto.BreakRule.MaxDrivingSeconds, dto.BreakRule.BreakSeconds),
            stops, vehicles, solution, locks, dto.Optimizer ?? new OptimizerSettings());
    }

    private static Dictionary<string, int> Index(IEnumerable<string> ids, string what)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (!d.TryAdd(id, d.Count)) throw new ProjectFileException($"{what} id「{id}」重複。");
        return d;
    }

    /// <summary>SHA-256 (hex) of a file, used to detect a changed road network.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Default project folder: %UserProfile%\Documents\MealRoute.</summary>
    public static string DefaultFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MealRoute");
}
