using Godot;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

public partial class VisualGalleryBuilder : Node3D
{
    private const float TileHeight = 0.35f;
    private readonly RandomNumberGenerator _rng = new();
    
    // Quick copy of the colors from GameManager
    private readonly Dictionary<int, Color> _tileColors = new()
    {
        { 0, Color.FromHtml("#170f2e") },
        { 1, Color.FromHtml("#241a44") },
        { 2, Color.FromHtml("#1c1438") },
        { 3, Color.FromHtml("#a78bfa") },
        { 4, Color.FromHtml("#fb2c46") },
        { 5, Color.FromHtml("#d946ef") },
        { 6, Color.FromHtml("#fbbf24") },
        { 7, Color.FromHtml("#22d3ee") },
        { 8, Color.FromHtml("#34d399") },
        { 9, Color.FromHtml("#f472b6") }
    };

    public override void _Ready()
    {
        BuildGallery();
    }

    private void BuildGallery()
    {
        var records = FetchHofGenomes();
        if (records.Count == 0) return;

        // Pick 5 representative generations
        List<GenomeDef> selection = new();
        int step = Mathf.Max(1, records.Count / 5);
        for (int i = 0; i < records.Count; i += step)
        {
            if (selection.Count < 5) selection.Add(records[i]);
        }
        if (selection.Count > 0 && selection[^1].Gen != records[^1].Gen)
            selection[^1] = records[^1]; // Make sure latest is included

        float zOffset = 0;
        int mapWidth = 0;

        foreach (var genome in selection)
        {
            mapWidth = Mathf.Max(40, genome.Width);
            int[,] map = GenerateLayoutGrid(genome);
            Node3D mapNode = GenerateGeometry(map, genome.Width, genome.Height);
            
            mapNode.Position = new Vector3(0, 0, zOffset);
            AddChild(mapNode);

            // Add 3D text label above the map
            var label = new Label3D();
            label.Text = $"Generation {genome.Gen}\nTraps: {genome.Traps:P0} | Enemies: {genome.Enemies:F1} | Heals: {genome.Heals:P0}";
            label.Position = new Vector3(genome.Width / 2f, 15f, zOffset + (genome.Height / 2f));
            label.PixelSize = 0.05f;
            label.Billboard = BaseMaterial3D.BillboardModeEnum.Enabled;
            AddChild(label);

            zOffset += genome.Height + 5; // Spacing for the next map
        }

        // Auto position camera to see the full gallery
        Camera3D cam = new Camera3D();
        cam.Position = new Vector3(mapWidth / 2f, 120, zOffset / 2f);
        cam.RotationDegrees = new Vector3(-90, 0, 0); // pointing straight down
        AddChild(cam);
        cam.Current = true;
    }

    private class GenomeDef
    {
        public int Gen;
        public int Width;
        public int Height;
        public int RoomAttempts;
        public float Traps;
        public float Enemies;
        public float Heals;
    }

    private List<GenomeDef> FetchHofGenomes()
    {
        var list = new List<GenomeDef>();
        string logPath = ProjectSettings.GlobalizePath("res://Backend/data/session_log.jsonl");
        if (!File.Exists(logPath)) return list;

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var doc = JsonDocument.Parse(line).RootElement;
            
            if (doc.TryGetProperty("passed", out var passEl) && passEl.GetBoolean())
            {
                var p = doc.GetProperty("params");
                list.Add(new GenomeDef
                {
                    Gen = doc.GetProperty("generation").GetInt32(),
                    Width = p.GetProperty("map_width").GetInt32(),
                    Height = p.GetProperty("map_height").GetInt32(),
                    RoomAttempts = p.GetProperty("room_attempts").GetInt32(),
                    Traps = p.GetProperty("trap_density").GetSingle(),
                    Enemies = p.GetProperty("enemy_difficulty_scale").GetSingle(),
                    Heals = p.GetProperty("heal_density").GetSingle()
                });
            }
        }
        return list;
    }

    private int[,] GenerateLayoutGrid(GenomeDef genome)
    {
        int W = Mathf.Max(40, genome.Width);
        int H = Mathf.Max(40, genome.Height);
        int[,] tiles = new int[H, W];
        var rooms = new List<(int X, int Y, int w, int h)>();
        _rng.Seed = (ulong)genome.Gen;

        for (int i = 0; i < genome.RoomAttempts; i++)
        {
            int w = _rng.RandiRange(4, 12);
            int h = _rng.RandiRange(4, 10);
            if (W - w - 2 <= 1 || H - h - 2 <= 1) continue;

            int x = _rng.RandiRange(1, W - w - 2);
            int y = _rng.RandiRange(1, H - h - 2);
            
            bool overlap = false;
            foreach (var o in rooms) 
                if (x < o.X + o.w + 1 && x + w + 1 > o.X && y < o.Y + o.h + 1 && y + h + 1 > o.Y) { overlap = true; break; }
            if (overlap) continue;

            rooms.Add((x, y, w, h));
            for (int ry = y; ry < y + h; ry++)
                for (int rx = x; rx < x + w; rx++)
                    tiles[ry, rx] = 1;
        }

        // Default room if fails
        if (rooms.Count == 0)
        {
            var r = (W / 2 - 4, H / 2 - 4, 8, 8);
            rooms.Add(r);
            for (int ry = r.Item2; ry < r.Item2 + r.h; ry++)
                for (int rx = r.X; rx < r.X + r.w; rx++)
                    tiles[ry, rx] = 1;
        }

        // Simplified connecting
        var prev = new Vector2I(rooms[0].X + rooms[0].w/2, rooms[0].Y + rooms[0].h/2);
        for (int i = 1; i < rooms.Count; i++) {
            var c = new Vector2I(rooms[i].X + rooms[i].w/2, rooms[i].Y + rooms[i].h/2);
            int cx = prev.X;
            while(cx != c.X) { tiles[prev.Y, cx] = 2; cx += System.Math.Sign(c.X - cx); tiles[prev.Y, cx] = 2; }
            int cy = prev.Y;
            while(cy != c.Y) { tiles[cy, c.X] = 2; cy += System.Math.Sign(c.Y - cy); tiles[cy, c.X] = 2; }
            prev = c;
        }

        // Place specials
        float trapCut = Mathf.Clamp(genome.Traps, 0f, 1f);
        float chestCut = trapCut + 0.25f; // rough simulation 
        float healCut = chestCut + Mathf.Clamp(genome.Heals, 0f, 1f);
        float enemyCut = healCut + Mathf.Clamp(genome.Enemies/10f, 0f, 1f); // just for visualization

        for (int i = 1; i < rooms.Count; i++)
        {
            var center = new Vector2I(rooms[i].X + rooms[i].w/2, rooms[i].Y + rooms[i].h/2);
            if (tiles[center.Y, center.X] != 1 && tiles[center.Y, center.X] != 2) continue;
            float roll = _rng.Randf();
            if (roll < trapCut) tiles[center.Y, center.X] = 4;
            else if (roll < enemyCut) tiles[center.Y, center.X] = 5;
            else if (roll < healCut) tiles[center.Y, center.X] = 9;
        }
        return tiles;
    }

    private Node3D GenerateGeometry(int[,] tiles, int mapWidth, int mapHeight)
    {
        Node3D root = new Node3D();
        int wallCount = 0, floorCount = 0, specialCount = 0;

        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                int type = tiles[y, x];
                if (type == 0) wallCount++;
                else if (type == 1 || type == 2) floorCount++;
                else specialCount++;
            }
        }

        MultiMesh wMM = CreateTileMM(wallCount, new Vector3(1.0f, 1.1f, 1.0f));
        MultiMesh fMM = CreateTileMM(floorCount, new Vector3(1.0f, TileHeight, 1.0f));
        MultiMesh sMM = CreateTileMM(specialCount, new Vector3(1.0f, TileHeight + 0.15f, 1.0f));

        root.AddChild(CreateInstance(wMM));
        root.AddChild(CreateInstance(fMM));
        root.AddChild(CreateInstance(sMM));

        int wIdx = 0, fIdx = 0, sIdx = 0;
        for (int y = 0; y < mapHeight; y++)
        {
            for (int x = 0; x < mapWidth; x++)
            {
                int type = tiles[y, x];
                var t = new Transform3D(Basis.Identity, new Vector3(x + 0.5f, 0, y + 0.5f));
                Color c = _tileColors.GetValueOrDefault(type, Colors.White);

                if (type == 0) { wMM.SetInstanceTransform(wIdx, t); wMM.SetInstanceColor(wIdx, c); wIdx++; }
                else if (type == 1 || type == 2) { fMM.SetInstanceTransform(fIdx, t); fMM.SetInstanceColor(fIdx, c); fIdx++; }
                else { sMM.SetInstanceTransform(sIdx, t); sMM.SetInstanceColor(sIdx, c); sIdx++; }
            }
        }
        return root;
    }

    private MultiMesh CreateTileMM(int cap, Vector3 box) => new MultiMesh {
        TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
        UseColors = true, Mesh = new BoxMesh { Size = box }, InstanceCount = cap
    };

    private MultiMeshInstance3D CreateInstance(MultiMesh mm) => new MultiMeshInstance3D {
        Multimesh = mm,
        MaterialOverride = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Metallic = 0.08f, Roughness = 0.75f }
    };
}
