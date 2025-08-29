using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Tilemaps;

// SpecBootstrapper builds a Unity scene from the generated JSON spec and script files.
// Workflow:
// 1) Tools → AI → Import From Spec...
// 2) Choose the JSON spec (output/*.json) and the scripts folder (output/scripts)
// 3) The tool copies scripts into Assets/Scripts/Generated, reloads assemblies, then constructs the scene(s)

namespace PlayAI.Editor
{
public static class SpecBootstrapper
{
        private const string GeneratedScriptsFolder = "Assets/Scripts/Generated";
        private const string GeneratedScenesFolder = "Assets/Scenes/Generated";
        private const string ProjectTagManagerPath = "ProjectSettings/TagManager.asset";
        private const string PendingSpecKey = "PlayAI.PendingSpecPath";
        private const string PendingScriptsKey = "PlayAI.PendingScriptsPath";
        private static HashSet<string> s_ProcessedSpriteAssets = new HashSet<string>();
        private static Dictionary<string, Type> s_ScriptTypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

        [MenuItem("Tools/AI/Import From Spec...")]
        private static void ImportFromSpecMenu()
        {
            string specPath = EditorUtility.OpenFilePanel("Select Game Spec JSON", Application.dataPath, "json");
            if (string.IsNullOrEmpty(specPath)) return;

            string scriptsFolder = EditorUtility.OpenFolderPanel("Select Scripts Folder (output/scripts)", Application.dataPath, "");
            if (string.IsNullOrEmpty(scriptsFolder)) return;

            PrepareScriptsAndQueueImport(specPath, scriptsFolder);
        }

        private static void BuildTilemapLayer(Dictionary<string, object> layerDict, int index, Dictionary<string, object> game)
        {
            string name = GetString(layerDict, "name") ?? $"Tilemap_{index}";
            string tilesetName = GetString(layerDict, "tileset");
            if (string.IsNullOrEmpty(tilesetName)) { Debug.LogWarning("SpecBootstrapper: Tilemap missing tileset."); return; }

            // Find tileset by name in game.tilesets
            var tilesets = GetList<object>(game, "tilesets");
            var tileset = tilesets?.Select(o => o as Dictionary<string, object>).FirstOrDefault(d => string.Equals(GetString(d, "name"), tilesetName, StringComparison.Ordinal));
            if (tileset == null) { Debug.LogWarning($"SpecBootstrapper: Tileset '{tilesetName}' not found."); return; }

            string atlasRel = GetString(tileset, "atlas");
            // Force pixels-per-unit to 32 so each 32x32 tile maps to 1 Unity unit
            float ppu = 32f;
            var tileSize = GetDict(tileset, "tileSize");
            int tileW = (int)GetFloat(tileSize, "x", 32f);
            int tileH = (int)GetFloat(tileSize, "y", 32f);

            // Ensure atlas imported as sprite-sheet sliced
            var atlasAssetPath = EnsureSpriteAssetAvailable(atlasRel, ppu);
            SliceAtlasIfNeeded(atlasAssetPath, tileW, tileH, ppu, GetString(tileset, "filterMode") ?? "Point", GetString(tileset, "compression") ?? "None");

            // Build name->sprite map from atlas and tileset.tiles coords
            var spritesByName = LoadTileSpritesFromAtlas(atlasAssetPath, tileset, tileW, tileH);
            if (spritesByName == null || spritesByName.Count == 0) { Debug.LogWarning("SpecBootstrapper: No tiles loaded from atlas."); }

            // Root Grid (reuse or create) with robust component ensure
            var gridTuple = GetOrCreateGrid();
            var gridGO = gridTuple.Item1;
            var grid = gridTuple.Item2;

            var tmGO = new GameObject(name);
            tmGO.transform.SetParent(gridGO.transform, false);
            var tilemap = tmGO.AddComponent<Tilemap>();
            var renderer = tmGO.AddComponent<TilemapRenderer>();

            string sortingLayer = GetString(layerDict, "sortingLayer");
            if (!string.IsNullOrEmpty(sortingLayer)) { TryAddSortingLayer(sortingLayer); renderer.sortingLayerName = sortingLayer; }
            renderer.sortingOrder = (int)GetFloat(layerDict, "orderInLayer", 0f);

            var origin = GetDict(layerDict, "origin");
            if (origin != null) tmGO.transform.position = new Vector3(GetFloat(origin, "x", 0f), GetFloat(origin, "y", 0f), 0f);

            var cellSz = GetDict(layerDict, "cellSize");
            if (cellSz != null)
            {
                try
                {
                    // Re-fetch to avoid stale handle across reloads
                    var g = gridGO != null ? gridGO.GetComponent<Grid>() : null;
                    if (g == null && gridGO != null) g = gridGO.AddComponent<Grid>();
                    if (g == null)
                    {
                        var created = GetOrCreateGrid();
                        gridGO = created.Item1;
                        g = created.Item2;
                    }
                    if (g != null)
                    {
                        g.cellSize = new Vector3(GetFloat(cellSz, "x", 1f), GetFloat(cellSz, "y", 1f), 0f);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"SpecBootstrapper: Failed to set Grid.cellSize: {ex.Message}");
                }
            }
            else
            {
                try
                {
                    var g = gridGO != null ? gridGO.GetComponent<Grid>() : null;
                    if (g == null && gridGO != null) g = gridGO.AddComponent<Grid>();
                    if (g != null)
                    {
                        float csx = tileW / Mathf.Max(1f, ppu);
                        float csy = tileH / Mathf.Max(1f, ppu);
                        g.cellSize = new Vector3(csx, csy, 0f);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"SpecBootstrapper: Failed to set default Grid.cellSize: {ex.Message}");
                }
            }

            // Grid from string rows with legend
            var gridObj = GetDict(layerDict, "grid");
            var rows = GetList<string>(gridObj, "rows");
            var legend = GetDict(gridObj, "legend"); // char -> tile name (string or null)
            string emptyChar = GetString(gridObj, "emptyChar") ?? ".";
            if (rows != null && rows.Count > 0)
            {
                int height = rows.Count;
                for (int y = 0; y < height; y++)
                {
                    string row = rows[height - 1 - y];
                    for (int x = 0; x < row.Length; x++)
                    {
                        string c = row.Substring(x, 1);
                        if (c == emptyChar) continue;
                        string tileName = null;
                        if (legend != null && legend.TryGetValue(c, out var mapped))
                        {
                            if (mapped == null) continue; // explicit skip
                            tileName = mapped.ToString();
                        }
                        else
                        {
                            tileName = c; // direct name
                        }
                        if (string.IsNullOrEmpty(tileName)) continue;
                        if (!spritesByName.TryGetValue(tileName, out var sprite) || sprite == null)
                        {
                            Debug.LogWarning($"SpecBootstrapper: Tile '{tileName}' not found in tileset '{tilesetName}'.");
                            continue;
                        }
                        var tile = ScriptableObject.CreateInstance<Tile>();
                        tile.sprite = sprite;
                        tilemap.SetTile(new Vector3Int(x, y, 0), tile);
                    }
                }
                // Force refresh so tiles render immediately in editor
                tilemap.RefreshAllTiles();
            }

            // Collider
            var col = GetDict(layerDict, "collider");
            string colType = GetString(col, "type") ?? "None";
            bool isTrigger = GetBool(col, "isTrigger", false);
            if (colType != "None")
            {
                var tileCol = tmGO.AddComponent<TilemapCollider2D>();
                tileCol.isTrigger = isTrigger;
                if (colType == "Composite")
                {
                    var rb = tmGO.AddComponent<Rigidbody2D>();
                    rb.bodyType = RigidbodyType2D.Static;
                    var comp = tmGO.AddComponent<CompositeCollider2D>();
                    tileCol.usedByComposite = true;
                    comp.geometryType = CompositeCollider2D.GeometryType.Polygons;
                }
            }
        }

        private static void SliceAtlasIfNeeded(string atlasAssetPath, int tileW, int tileH, float ppu, string filterMode, string compression)
        {
            var importer = AssetImporter.GetAtPath(atlasAssetPath) as TextureImporter;
            if (importer == null) return;
            bool changed = false;
            if (importer.textureType != TextureImporterType.Sprite) { importer.textureType = TextureImporterType.Sprite; changed = true; }
            if (importer.spriteImportMode != SpriteImportMode.Multiple) { importer.spriteImportMode = SpriteImportMode.Multiple; changed = true; }
            if (Mathf.Abs(importer.spritePixelsPerUnit - ppu) > 0.001f) { importer.spritePixelsPerUnit = ppu; changed = true; }

            // Filter/Compression
            var desiredFilter = filterMode == "Point" ? FilterMode.Point : filterMode == "Trilinear" ? FilterMode.Trilinear : FilterMode.Bilinear;
            if (importer.filterMode != desiredFilter) { importer.filterMode = desiredFilter; changed = true; }
            var desiredCompression = compression == "None" ? TextureImporterCompression.Uncompressed :
                compression == "Low" ? TextureImporterCompression.CompressedLQ :
                compression == "High" ? TextureImporterCompression.CompressedHQ : TextureImporterCompression.Compressed;
            if (importer.textureCompression != desiredCompression) { importer.textureCompression = desiredCompression; changed = true; }

            // Define slicing rects
            Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasAssetPath);
            if (tex != null)
            {
                int cols = tex.width / tileW;
                int rows = tex.height / tileH;
                var metas = new List<SpriteMetaData>();
                metas.Capacity = cols * rows;
                for (int yTop = 0; yTop < rows; yTop++)
                {
                    int yBottom = rows - 1 - yTop; // Unity rect uses bottom-left origin; our names use top-left origin
                    for (int x = 0; x < cols; x++)
                    {
                        var smd = new SpriteMetaData();
                        smd.rect = new Rect(x * tileW, yBottom * tileH, tileW, tileH);
                        smd.name = $"tile_{x}_{yTop}";
                        smd.alignment = (int)SpriteAlignment.Center;
                        metas.Add(smd);
                    }
                }
                if (importer.spritesheet == null || importer.spritesheet.Length != metas.Count)
                {
                    importer.spritesheet = metas.ToArray();
                    changed = true;
                }
                // Ensure point filtering on importer settings too
                if (importer.filterMode != FilterMode.Point)
                {
                    importer.filterMode = FilterMode.Point;
                    changed = true;
                }
            }
            if (changed)
            {
                importer.SaveAndReimport();
            }
        }

        private static Dictionary<string, Sprite> LoadTileSpritesFromAtlas(string atlasAssetPath, Dictionary<string, object> tileset, int tileW, int tileH)
        {
            var map = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            var tiles = GetList<object>(tileset, "tiles");
            if (tiles == null) return map;
            var all = AssetDatabase.LoadAllAssetsAtPath(atlasAssetPath);
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasAssetPath);
            foreach (var tObj in tiles)
            {
                var t = tObj as Dictionary<string, object>;
                if (t == null) continue;
                string name = GetString(t, "name");
                int tx = (int)GetFloat(t, "x", 0f);
                int ty = (int)GetFloat(t, "y", 0f);
                // tile coordinates in reference are from top-left origin
                string expected = $"tile_{tx / tileW}_{ty / tileH}";
                var sprite = all.OfType<Sprite>().FirstOrDefault(s => string.Equals(s.name, expected, StringComparison.Ordinal));
                if (sprite == null && tex != null)
                {
                    // Fallback: create a runtime sprite from the atlas if named slice not found
                    int yBottom = tex.height - ty - tileH; // convert top-left to bottom-left origin
                    if (yBottom >= 0 && yBottom + tileH <= tex.height)
                    {
                        try
                        {
                            var rect = new Rect(tx, yBottom, tileW, tileH);
                            sprite = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 32f);
                        }
                        catch { }
                    }
                }
                if (sprite != null && !string.IsNullOrEmpty(name)) map[name] = sprite;
            }
            return map;
        }
        private static void PrepareScriptsAndQueueImport(string specPath, string scriptsFolder)
        {
            Directory.CreateDirectory(GeneratedScriptsFolder);

            // Copy all .cs files
            foreach (string src in Directory.GetFiles(scriptsFolder, "*.cs", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(GeneratedScriptsFolder, Path.GetFileName(src));
                File.Copy(src, dst, true);
            }

            EditorPrefs.SetString(PendingSpecKey, specPath);
            EditorPrefs.SetString(PendingScriptsKey, scriptsFolder);
            AssetDatabase.Refresh();
        }

        [InitializeOnLoadMethod]
        private static void InitOnLoad()
        {
            AssemblyReloadEvents.afterAssemblyReload -= ContinueImportIfQueued;
            AssemblyReloadEvents.afterAssemblyReload += ContinueImportIfQueued;
        }

        private static void ContinueImportIfQueued()
        {
            string specPath = EditorPrefs.GetString(PendingSpecKey, string.Empty);
            if (string.IsNullOrEmpty(specPath)) return;

            try
            {
                s_ProcessedSpriteAssets.Clear();
                BuildScriptTypeCache();
                string json = File.ReadAllText(specPath);
                if (!string.IsNullOrEmpty(json) && json[0] == '\uFEFF')
                {
                    json = json.Substring(1); // strip UTF-8 BOM if present
                }
                if (string.IsNullOrWhiteSpace(json))
                {
                    Debug.LogError("SpecBootstrapper: Spec file is empty.");
                    return;
                }
                if (json.Length > 10_000_000)
                {
                    Debug.LogError($"SpecBootstrapper: Spec file is unexpectedly large ({json.Length} chars). Aborting to avoid memory issues. Ensure you selected the generated spec JSON, not a project file.");
                    return;
                }
                var trimmed = json.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] != '{')
                {
                    Debug.LogError("SpecBootstrapper: Spec does not look like a JSON object. Ensure you selected the correct spec file.");
                    return;
                }
                // Try MiniJSON first; if it fails, fallback to Newtonsoft for robustness.
                var root = MiniJSON.Deserialize(json) as Dictionary<string, object>;
                if (root == null)
                {
                    try
                    {
                        var jroot = Newtonsoft.Json.Linq.JObject.Parse(json);
                        root = jroot.ToObject<Dictionary<string, object>>();
                    }
                    catch
                    {
                        Debug.LogError("SpecBootstrapper: Failed to parse spec JSON root.");
                        var previewLen = Math.Min(800, json.Length);
                        Debug.LogError($"Spec preview (first {previewLen} chars):\n" + json.Substring(0, previewLen));
                        return;
                    }
                }

                var game = GetDict(root, "game");
                if (game == null)
                {
                    Debug.LogError("SpecBootstrapper: Missing 'game' object in spec.");
                    return;
                }

                ApplyProjectSettings(game);
                BuildScenes(game);
                AssetDatabase.SaveAssets();
                Resources.UnloadUnusedAssets();
            }
            catch (Exception ex)
            {
                Debug.LogError($"SpecBootstrapper: Exception while importing spec: {ex}");
            }
            finally
            {
                EditorPrefs.DeleteKey(PendingSpecKey);
                EditorPrefs.DeleteKey(PendingScriptsKey);
                EditorUtility.ClearProgressBar();
                s_ProcessedSpriteAssets.Clear();
            }
        }

        private static void BuildScriptTypeCache()
        {
            s_ScriptTypeCache.Clear();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var asm in assemblies)
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    // Cache by full name and simple name (last one wins; prefer Assembly-CSharp later)
                    if (!string.IsNullOrEmpty(t.FullName)) s_ScriptTypeCache[t.FullName] = t;
                    if (!string.IsNullOrEmpty(t.Name)) s_ScriptTypeCache[t.Name] = t;
                }
            }
            // Prefer Assembly-CSharp variants by overwriting
            foreach (var asm in assemblies.Where(a => string.Equals(a.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal)))
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t == null) continue;
                    if (!typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (!string.IsNullOrEmpty(t.FullName)) s_ScriptTypeCache[t.FullName] = t;
                    if (!string.IsNullOrEmpty(t.Name)) s_ScriptTypeCache[t.Name] = t;
                }
            }
        }

        private static void ApplyProjectSettings(Dictionary<string, object> game)
        {
            var settings = GetDict(game, "settings");
            if (settings == null) return;

            var tags = GetList<string>(settings, "tags");
            var layers = GetList<string>(settings, "layers");
            var sortingLayers = GetList<string>(settings, "sortingLayers");

            if (tags != null)
            {
                foreach (string t in tags) TryAddTag(t);
            }
            if (layers != null)
            {
                foreach (string l in layers) TryAddLayer(l);
            }
            if (sortingLayers != null)
            {
                foreach (string s in sortingLayers) TryAddSortingLayer(s);
            }

            // Input handling: leave as-is (template defaults to Both)
        }

        private static void BuildScenes(Dictionary<string, object> game)
        {
            var scenes = GetList<object>(game, "scenes");
            if (scenes == null || scenes.Count == 0)
            {
                Debug.LogWarning("SpecBootstrapper: No scenes found in spec.");
                return;
            }

            Directory.CreateDirectory(GeneratedScenesFolder);

            int index = 0;
            foreach (var sObj in scenes)
            {
                var sceneDict = sObj as Dictionary<string, object>;
                if (sceneDict == null) continue;

                string sceneName = GetString(sceneDict, "name") ?? $"GeneratedScene_{index}";
                string scenePath = Path.Combine(GeneratedScenesFolder, sceneName + ".unity");

                EditorUtility.DisplayProgressBar("Building Scene", sceneName, 0.1f);

                var newScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                newScene.name = sceneName;

                // Camera
                ApplyCameraSettings(sceneDict);

                // Background color
                string bg = GetString(sceneDict, "backgroundColor");
                if (TryParseColor(bg, out var bgColor))
                {
                    var cam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
                    if (cam != null)
                    {
                        cam.clearFlags = CameraClearFlags.SolidColor;
                        cam.backgroundColor = bgColor;
                    }
                }

                // UI Canvas + EventSystem
                ApplyUISettings(sceneDict);

                // GameObjects
                var gameObjects = GetList<object>(sceneDict, "gameObjects");
                if (gameObjects != null)
                {
                    int total = gameObjects.Count;
                    for (int i = 0; i < total; i++)
                    {
                        EditorUtility.DisplayProgressBar("Placing GameObjects", sceneName, 0.2f + 0.6f * (i / Mathf.Max(1f, (float)total)));
                        var goDict = gameObjects[i] as Dictionary<string, object>;
                        if (goDict == null) continue;
                        BuildGameObject(goDict);
                    }
                }

                // Post placement: align dynamic objects tagged Player/Enemy/Coin on top of nearest ground
                TryAlignObjectsToGround();

                // Tilemaps
                var tilemapsArr = GetList<object>(sceneDict, "tilemaps");
                if (tilemapsArr != null)
                {
                    int idx = 0;
                    foreach (var tObj in tilemapsArr)
                    {
                        var tDict = tObj as Dictionary<string, object>;
                        if (tDict == null) continue;
                        BuildTilemapLayer(tDict, idx++, game);
                    }
                }

                // UI elements
                var ui = GetList<object>(sceneDict, "ui");
                if (ui != null)
                {
                    foreach (var uiObj in ui)
                    {
                        var uiDict = uiObj as Dictionary<string, object>;
                        if (uiDict == null) continue;
                        BuildUIElement(uiDict);
                    }
                }

                EditorSceneManager.SaveScene(newScene, scenePath);
                AddSceneToBuildSettings(scenePath);
                try { EditorSceneManager.CloseScene(newScene, true); } catch { }
                index++;
            }

            EditorUtility.ClearProgressBar();
        }

        private static void ApplyCameraSettings(Dictionary<string, object> sceneDict)
        {
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGO = new GameObject("Main Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = "MainCamera";
            }

            var camDict = GetDict(sceneDict, "camera");
            if (camDict != null)
            {
                bool orthographic = GetBool(camDict, "orthographic", true);
                cam.orthographic = orthographic;
                float size = GetFloat(camDict, "size", 5f);
                cam.orthographicSize = size;
                var pos = GetDict(camDict, "position");
                if (pos != null)
                {
                    cam.transform.position = new Vector3(GetFloat(pos, "x", 0f), GetFloat(pos, "y", 0f), -10f);
                }
            }
        }

        private static void ApplyUISettings(Dictionary<string, object> sceneDict)
        {
            var canvasGO = new GameObject("Canvas");
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvasGO.AddComponent<CanvasScaler>();
            canvasGO.AddComponent<GraphicRaycaster>();

            var uiSettings = GetDict(sceneDict, "uiSettings");
            if (uiSettings != null)
            {
                var renderModeStr = GetString(uiSettings, "renderMode");
                if (!string.IsNullOrEmpty(renderModeStr))
                {
                    if (Enum.TryParse<RenderMode>(renderModeStr, out var rm))
                    {
                        canvas.renderMode = rm;
                        if (rm == RenderMode.ScreenSpaceCamera)
                        {
                            var mainCam = Camera.main ?? UnityEngine.Object.FindFirstObjectByType<Camera>();
                            if (mainCam != null) canvas.worldCamera = mainCam;
                        }
                    }
                }
                var refRes = GetDict(uiSettings, "referenceResolution");
                if (refRes != null)
                {
                    var scaler = canvasGO.GetComponent<CanvasScaler>();
                    if (scaler != null)
                    {
                        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                        scaler.referenceResolution = new Vector2(GetFloat(refRes, "x", 1920f), GetFloat(refRes, "y", 1080f));
                    }
                }
            }

            // EventSystem
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<StandaloneInputModule>();
        }

        private static void BuildGameObject(Dictionary<string, object> goDict)
        {
            string name = GetString(goDict, "name") ?? GetString(goDict, "id") ?? "GameObject";
            var go = new GameObject(name);

            // Tag & Layer
            string tag = GetString(goDict, "tag");
            if (!string.IsNullOrEmpty(tag)) TryAddTag(tag);
            if (!string.IsNullOrEmpty(tag)) SafeSetTag(go, tag);

            string layerName = GetString(goDict, "layer");
            if (!string.IsNullOrEmpty(layerName)) TryAddLayer(layerName);
            if (!string.IsNullOrEmpty(layerName)) go.layer = LayerMask.NameToLayer(layerName);

            // Transform
            var transformDict = GetDict(goDict, "transform");
            if (transformDict != null)
            {
                var pos = GetDict(transformDict, "position");
                if (pos != null) go.transform.position = new Vector3(GetFloat(pos, "x", 0f), GetFloat(pos, "y", 0f), 0f);
                go.transform.rotation = Quaternion.Euler(0f, 0f, GetFloat(transformDict, "rotation", 0f));
                var scale = GetDict(transformDict, "scale");
                if (scale != null) go.transform.localScale = new Vector3(GetFloat(scale, "x", 1f), GetFloat(scale, "y", 1f), 1f);
            }

            // Sprite
            var spriteDict = GetDict(goDict, "sprite");
            if (spriteDict != null)
            {
                var sr = go.AddComponent<SpriteRenderer>();
                string relPath = GetString(spriteDict, "path");
                float? ppu = TryGetFloat(spriteDict, "pixelsPerUnit");
                string assetPath = EnsureSpriteAssetAvailable(relPath, ppu);
                var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                if (sprite == null)
                {
                    Debug.LogWarning($"SpecBootstrapper: Sprite not found at '{assetPath}'.");
                }
                else
                {
                    sr.sprite = sprite;
                }
                string sortingLayer = GetString(spriteDict, "sortingLayer");
                if (!string.IsNullOrEmpty(sortingLayer)) TryAddSortingLayer(sortingLayer);
                if (!string.IsNullOrEmpty(sortingLayer)) sr.sortingLayerName = sortingLayer;
                sr.sortingOrder = (int)GetFloat(spriteDict, "orderInLayer", 0f);
            }

            // Physics
            var physics = GetDict(goDict, "physics");
            if (physics != null)
            {
                var rbDict = GetDict(physics, "rigidbody2D");
                Rigidbody2D rb = null;
                if (rbDict != null)
                {
                    rb = go.AddComponent<Rigidbody2D>();
                    string bodyType = GetString(rbDict, "bodyType");
                    if (!string.IsNullOrEmpty(bodyType))
                    {
                        if (Enum.TryParse<RigidbodyType2D>(bodyType, out var bt)) rb.bodyType = bt;
                    }
                    rb.gravityScale = GetFloat(rbDict, "gravityScale", rb.gravityScale);

                    var constraints = GetList<string>(rbDict, "constraints");
                    if (constraints != null && constraints.Count > 0)
                    {
                        RigidbodyConstraints2D cons = RigidbodyConstraints2D.None;
                        foreach (string c in constraints)
                        {
                            switch (c)
                            {
                                case "FreezeRotationZ": cons |= RigidbodyConstraints2D.FreezeRotation; break;
                                case "FreezePositionX": cons |= RigidbodyConstraints2D.FreezePositionX; break;
                                case "FreezePositionY": cons |= RigidbodyConstraints2D.FreezePositionY; break;
                            }
                        }
                        rb.constraints = cons;
                    }
                }

                var colDict = GetDict(physics, "collider2D");
                if (colDict != null)
                {
                    string type = GetString(colDict, "type");
                    bool autoSize = GetBool(colDict, "autoSize", true);
                    var offsetDict = GetDict(colDict, "offset");
                    Vector2? desiredOffset = null;
                    if (offsetDict != null)
                    {
                        desiredOffset = new Vector2(GetFloat(offsetDict, "x", 0f), GetFloat(offsetDict, "y", 0f));
                    }
                    if (type == "Box")
                    {
                        var box = go.AddComponent<BoxCollider2D>();
                        var size = GetDict(colDict, "size");
                        if (!autoSize && size != null)
                        {
                            box.size = new Vector2(GetFloat(size, "x", 1f), GetFloat(size, "y", 1f));
                        }
                        else
                        {
                            // Auto-size from sprite bounds if available
                            var sr = go.GetComponent<SpriteRenderer>();
                            if (sr != null && sr.sprite != null)
                            {
                                // Use sprite bounds size (local units) as base collider size
                                box.size = sr.sprite.bounds.size;
                            }
                        }
                        box.isTrigger = GetBool(colDict, "isTrigger", box.isTrigger);
                        if (desiredOffset.HasValue) box.offset = desiredOffset.Value;
                    }
                    else if (type == "Circle")
                    {
                        var circle = go.AddComponent<CircleCollider2D>();
                        float? r = TryGetFloat(colDict, "radius");
                        if (!autoSize && r.HasValue)
                        {
                            circle.radius = r.Value;
                        }
                        else
                        {
                            // Auto-size from sprite bounds if available
                            var sr = go.GetComponent<SpriteRenderer>();
                            if (sr != null && sr.sprite != null)
                            {
                                var ext = sr.sprite.bounds.extents;
                                circle.radius = Mathf.Min(ext.x, ext.y);
                            }
                        }
                        circle.isTrigger = GetBool(colDict, "isTrigger", circle.isTrigger);
                        if (desiredOffset.HasValue) circle.offset = desiredOffset.Value;
                    }
                    else if (type == "Polygon")
                    {
                        var poly = go.AddComponent<PolygonCollider2D>();
                        if (desiredOffset.HasValue) poly.offset = desiredOffset.Value;
                    }
                }
            }

            // Attach scripts
            var scripts = GetList<object>(goDict, "scripts");
            if (scripts != null)
            {
                foreach (var sObj in scripts)
                {
                    var sDict = sObj as Dictionary<string, object>;
                    if (sDict == null) continue;
                    string scriptName = GetString(sDict, "name");
                    if (string.IsNullOrEmpty(scriptName)) continue;
                    AttachScriptByName(go, scriptName, GetDict(sDict, "parameters"));
                }
            }
        }

        

        private static void BuildUIElement(Dictionary<string, object> ui)
        {
            string type = GetString(ui, "type");
            string name = GetString(ui, "name") ?? GetString(ui, "id") ?? "UIElement";

            Transform canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>()?.transform;
            if (canvas == null)
            {
                Debug.LogWarning("SpecBootstrapper: Canvas not found. Creating a default one.");
                ApplyUISettings(new Dictionary<string, object>());
                canvas = UnityEngine.Object.FindFirstObjectByType<Canvas>()?.transform;
            }

            var go = new GameObject(name);
            go.transform.SetParent(canvas, false);

            var rect = go.AddComponent<RectTransform>();
            var rt = GetDict(ui, "rectTransform");
            if (rt != null)
            {
                var anchorMin = GetDict(rt, "anchorMin");
                var anchorMax = GetDict(rt, "anchorMax");
                var pivot = GetDict(rt, "pivot");
                var position = GetDict(rt, "position");
                var sizeDelta = GetDict(rt, "sizeDelta");
                if (anchorMin != null) rect.anchorMin = new Vector2(GetFloat(anchorMin, "x", 0.5f), GetFloat(anchorMin, "y", 0.5f));
                if (anchorMax != null) rect.anchorMax = new Vector2(GetFloat(anchorMax, "x", 0.5f), GetFloat(anchorMax, "y", 0.5f));
                if (pivot != null) rect.pivot = new Vector2(GetFloat(pivot, "x", 0.5f), GetFloat(pivot, "y", 0.5f));
                if (position != null) rect.anchoredPosition = new Vector2(GetFloat(position, "x", 0f), GetFloat(position, "y", 0f));
                if (sizeDelta != null) rect.sizeDelta = new Vector2(GetFloat(sizeDelta, "x", 160f), GetFloat(sizeDelta, "y", 40f));
            }
            else
            {
                // Centered defaults so UI is visible
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(300f, 80f);
            }

            if (type == "text")
            {
                var text = go.AddComponent<Text>();
                text.text = GetString(ui, "text") ?? string.Empty;
                text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                text.fontSize = (int)GetFloat(ui, "fontSize", 24);
                if (TryParseColor(GetString(ui, "color"), out var c)) text.color = c;
                text.alignment = ParseTextAnchor(GetString(ui, "alignment"));

                AttachUIScripts(go, ui);
            }
            else if (type == "image")
            {
                var img = go.AddComponent<Image>();
                string relPath = GetString(ui, "sprite");
                if (!string.IsNullOrEmpty(relPath))
                {
                    string assetPath = EnsureSpriteAssetAvailable(relPath, null);
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    img.sprite = sprite;
                    if (sprite != null) img.SetNativeSize();
                }
                AttachUIScripts(go, ui);
            }
            else if (type == "button")
            {
                var img = go.AddComponent<Image>();
                var btn = go.AddComponent<Button>();
                string relPath = GetString(ui, "sprite");
                if (!string.IsNullOrEmpty(relPath))
                {
                    string assetPath = EnsureSpriteAssetAvailable(relPath, null);
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
                    img.sprite = sprite;
                    if (sprite != null) img.SetNativeSize();
                }
                AttachUIScripts(go, ui);
            }
        }

        private static void TryAlignObjectsToGround()
        {
            var all = UnityEngine.Object.FindObjectsOfType<GameObject>();
            var groundColliders = new List<Collider2D>();
            int groundLayer = LayerMask.NameToLayer("Ground");
            foreach (var g in all)
            {
                if (g == null) continue;
                if (g.CompareTag("Ground") || (groundLayer >= 0 && g.layer == groundLayer))
                {
                    var cols = g.GetComponentsInChildren<Collider2D>();
                    if (cols != null && cols.Length > 0) groundColliders.AddRange(cols);
                }
            }

            if (groundColliders.Count == 0) return;

            foreach (var g in all)
            {
                if (g == null) continue;
                if (g.CompareTag("Ground")) continue;
                var rb = g.GetComponent<Rigidbody2D>();
                if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic) continue;
                var sr = g.GetComponent<SpriteRenderer>();
                if (sr == null || sr.sprite == null) continue;

                Bounds b = sr.bounds;
                float centerX = b.center.x;
                float bottomY = b.min.y;

                float bestTop = float.NegativeInfinity;
                foreach (var col in groundColliders)
                {
                    if (col == null) continue;
                    var gb = col.bounds;
                    if (centerX < gb.min.x || centerX > gb.max.x) continue;
                    if (gb.max.y > bestTop) bestTop = gb.max.y;
                }

                if (bestTop > float.NegativeInfinity)
                {
                    float dy = bestTop - bottomY;
                    if (Mathf.Abs(dy) > 0.001f)
                    {
                        var p = g.transform.position;
                        g.transform.position = new Vector3(p.x, p.y + dy, p.z);
                    }
                }
            }
        }

        private static void AttachUIScripts(GameObject go, Dictionary<string, object> ui)
        {
            var scripts = GetList<object>(ui, "scripts");
            if (scripts == null) return;
            foreach (var sObj in scripts)
            {
                var sDict = sObj as Dictionary<string, object>;
                if (sDict == null) continue;
                string scriptName = GetString(sDict, "name");
                if (string.IsNullOrEmpty(scriptName)) continue;
                AttachScriptByName(go, scriptName, GetDict(sDict, "parameters"));
            }
        }

        private static void AttachScriptByName(GameObject go, string scriptName, Dictionary<string, object> parameters)
        {
            // Resolve type via cache first
            s_ScriptTypeCache.TryGetValue(scriptName, out var type);
            if (type == null)
            {
                // Fallback: try simple name match in cache (case-insensitive)
                var kv = s_ScriptTypeCache.FirstOrDefault(p => string.Equals(p.Key, scriptName, StringComparison.OrdinalIgnoreCase));
                type = kv.Value;
            }

            if (type == null)
            {
                // Fallback: scan assemblies once more for rare cases
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                    })
                    .FirstOrDefault(t => t != null && typeof(MonoBehaviour).IsAssignableFrom(t) && (t.Name == scriptName || t.FullName == scriptName));
                if (type != null)
                {
                    if (!string.IsNullOrEmpty(type.FullName)) s_ScriptTypeCache[type.FullName] = type;
                    if (!string.IsNullOrEmpty(type.Name)) s_ScriptTypeCache[type.Name] = type;
                }
            }

            if (type == null)
            {
                Debug.LogWarning($"SpecBootstrapper: Could not find script type '{scriptName}'. Ensure it compiled successfully.");
            return;
        }

            var mb = go.AddComponent(type);
            if (mb == null) return;

            // Map parameters to public fields/properties by name
            if (parameters != null && parameters.Count > 0)
            {
                foreach (var kv in parameters)
                {
                    string fieldName = kv.Key;
                    object value = kv.Value;

                    var field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var prop = type.GetProperty(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    try
                    {
                        if (field != null)
                        {
                            object converted = ConvertParameterValue(value, field.FieldType, go, fieldName, parameters);
                            field.SetValue(mb, converted);
                        }
                        else if (prop != null && prop.CanWrite)
                        {
                            object converted = ConvertParameterValue(value, prop.PropertyType, go, fieldName, parameters);
                            prop.SetValue(mb, converted);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"SpecBootstrapper: Failed to set parameter '{fieldName}' on '{scriptName}': {ex.Message}");
                    }
                }
            }

            // Post-wiring: auto-assign common references if still null or default
            try
            {
                AutoWireCommonFields(go, mb, type, parameters);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"SpecBootstrapper: Auto-wiring on '{scriptName}' failed: {ex.Message}");
            }
        }

        private static object ConvertParameterValue(object value, Type targetType, GameObject owner, string memberName, Dictionary<string, object> allParams)
        {
            if (value == null) return null;
            if (targetType == typeof(string)) return value.ToString();
            if (targetType == typeof(int)) return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float)) return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(Vector2))
            {
                var d = value as Dictionary<string, object>;
                if (d != null) return new Vector2(GetFloat(d, "x", 0f), GetFloat(d, "y", 0f));
            }
            if (targetType == typeof(Vector3))
            {
                var d = value as Dictionary<string, object>;
                if (d != null) return new Vector3(GetFloat(d, "x", 0f), GetFloat(d, "y", 0f), GetFloat(d, "z", 0f));
            }
            if (targetType == typeof(LayerMask))
            {
                // Value may be a string layer name
                if (value is string s) return LayerMask.GetMask(s);
                // Or an array of layer names
                if (value is List<object> list)
                {
                    string[] names = list.Select(o => o?.ToString()).Where(n => !string.IsNullOrEmpty(n)).ToArray();
                    return LayerMask.GetMask(names);
                }
            }
            // Component or GameObject/Transform resolution from hints
            if (typeof(Component).IsAssignableFrom(targetType))
            {
                var go = ResolveGameObjectReference(value, owner);
                if (go != null)
                {
                    var comp = go.GetComponent(targetType);
                    if (comp != null) return comp;
                }
                // If asking for Transform, allow returning owner's Transform for common names
                if (targetType == typeof(Transform))
                {
                    // Special-case ground check creation if requested by name
                    if (!HasNonEmptyValue(value))
                    {
                        var created = MaybeCreateGroundCheck(owner, allParams);
                        if (created != null) return created.transform;
                    }
                }
                return null;
            }
            if (targetType == typeof(GameObject))
            {
                var go = ResolveGameObjectReference(value, owner);
                if (go != null) return go;
            }
            return value;
        }

        private static bool HasNonEmptyValue(object value)
        {
            if (value == null) return false;
            if (value is string s) return !string.IsNullOrWhiteSpace(s);
            if (value is Dictionary<string, object> d) return d.Count > 0;
            return true;
        }

        private static GameObject ResolveGameObjectReference(object hint, GameObject context)
        {
            if (hint == null) return null;
            // String hints: try child path, name, then tag
            if (hint is string s)
            {
                if (context != null)
                {
                    var child = context.transform.Find(s);
                    if (child != null) return child.gameObject;
                    // case-insensitive child name search
                    var foundChild = context.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => string.Equals(t.name, s, StringComparison.OrdinalIgnoreCase));
                    if (foundChild != null) return foundChild.gameObject;
                }
                var byName = GameObject.Find(s);
                if (byName != null) return byName;
                try { var byTag = GameObject.FindWithTag(s); if (byTag != null) return byTag; } catch { }
            }
            // Object hints
            var dct = hint as Dictionary<string, object>;
            if (dct != null)
            {
                string path = GetString(dct, "path");
                string childName = GetString(dct, "child");
                string name = GetString(dct, "name");
                string tag = GetString(dct, "tag");
                if (!string.IsNullOrEmpty(path) && context != null)
                {
                    var t = context.transform.Find(path);
                    if (t != null) return t.gameObject;
                }
                if (!string.IsNullOrEmpty(childName) && context != null)
                {
                    var t = context.GetComponentsInChildren<Transform>(true).FirstOrDefault(x => string.Equals(x.name, childName, StringComparison.OrdinalIgnoreCase));
                    if (t != null) return t.gameObject;
                }
                if (!string.IsNullOrEmpty(name))
                {
                    var go = GameObject.Find(name);
                    if (go != null) return go;
                }
                if (!string.IsNullOrEmpty(tag))
                {
                    try { var byTag = GameObject.FindWithTag(tag); if (byTag != null) return byTag; } catch { }
                }
            }
            return null;
        }

        private static void AutoWireCommonFields(GameObject owner, object component, Type type, Dictionary<string, object> parameters)
        {
            // Ground LayerMask default
            var groundMaskField = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(LayerMask) && f.Name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0);
            if (groundMaskField != null)
            {
                var val = (LayerMask)groundMaskField.GetValue(component);
                if (val.value == 0)
                {
                    int groundLayer = LayerMask.NameToLayer("Ground");
                    if (groundLayer >= 0)
                    {
                        groundMaskField.SetValue(component, LayerMask.GetMask("Ground"));
                    }
                }
            }

            // Ground check Transform default
            var groundCheckField = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(Transform) && f.Name.IndexOf("groundcheck", StringComparison.OrdinalIgnoreCase) >= 0);
            if (groundCheckField != null)
            {
                var cur = groundCheckField.GetValue(component) as Transform;
                if (cur == null)
                {
                    var created = MaybeCreateGroundCheck(owner, parameters);
                    if (created != null) groundCheckField.SetValue(component, created.transform);
                }
            }

            // Rigidbody2D self-assign
            var rbField = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => f.FieldType == typeof(Rigidbody2D));
            if (rbField != null && rbField.GetValue(component) == null)
            {
                var rb = owner.GetComponent<Rigidbody2D>();
                if (rb != null) rbField.SetValue(component, rb);
            }
        }

        private static GameObject MaybeCreateGroundCheck(GameObject owner, Dictionary<string, object> parameters)
        {
            if (owner == null) return null;
            // Try use provided offset
            Vector2 offset = new Vector2(0f, -0.2f);
            if (parameters != null)
            {
                var off = parameters.ContainsKey("groundCheckLocalOffset") ? parameters["groundCheckLocalOffset"] as Dictionary<string, object> : null;
                if (off != null)
                {
                    offset = new Vector2(GetFloat(off, "x", offset.x), GetFloat(off, "y", offset.y));
                }
            }
            var sr = owner.GetComponent<SpriteRenderer>();
            if (sr != null && sr.sprite != null)
            {
                // Adjust based on sprite world extents
                offset = new Vector2(offset.x, -Mathf.Max(0.1f, sr.bounds.extents.y * 0.9f));
            }
            var gc = new GameObject("GroundCheck");
            gc.transform.SetParent(owner.transform, false);
            gc.transform.localPosition = new Vector3(offset.x, offset.y, 0f);
            return gc;
        }

        private static string EnsureSpriteAssetAvailable(string relPath, float? pixelsPerUnit)
        {
            if (string.IsNullOrEmpty(relPath)) return null;

            // Preferred location inside Assets
            string assetsRel = relPath.Replace("\\", "/");
            if (!assetsRel.StartsWith("Assets/")) assetsRel = "Assets/" + assetsRel;

            bool didCopy = false;
            if (!File.Exists(assetsRel))
            {
                // Try copy from project root (../relPath) if present
                string rootCandidate = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? string.Empty, relPath).Replace("\\", "/");
                Directory.CreateDirectory(Path.GetDirectoryName(assetsRel) ?? "Assets");
                if (File.Exists(rootCandidate))
                {
                    File.Copy(rootCandidate, assetsRel, true);
                    didCopy = true;
                }
            }
            
            if (didCopy || !s_ProcessedSpriteAssets.Contains(assetsRel))
            {
                AssetDatabase.ImportAsset(assetsRel);
                s_ProcessedSpriteAssets.Add(assetsRel);
            }

            var texImporter = AssetImporter.GetAtPath(assetsRel) as TextureImporter;
            if (texImporter != null)
            {
                bool needsReimport = false;
                if (texImporter.textureType != TextureImporterType.Sprite)
                {
                    texImporter.textureType = TextureImporterType.Sprite;
                    needsReimport = true;
                }
                if (pixelsPerUnit.HasValue && Mathf.Abs(texImporter.spritePixelsPerUnit - pixelsPerUnit.Value) > 0.001f)
                {
                    texImporter.spritePixelsPerUnit = pixelsPerUnit.Value;
                    needsReimport = true;
                }
                if (needsReimport)
                {
                    texImporter.SaveAndReimport();
                }
            }

            return assetsRel;
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            if (scenes.Any(s => s.path == scenePath)) return;
            scenes.Add(new EditorBuildSettingsScene(scenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        private static void TryAddTag(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            if (UnityEditorInternal.InternalEditorUtility.tags.Contains(tag)) return;
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath(ProjectTagManagerPath)[0]);
            var tagsProp = tagManager.FindProperty("tags");
            tagsProp.InsertArrayElementAtIndex(tagsProp.arraySize);
            tagsProp.GetArrayElementAtIndex(tagsProp.arraySize - 1).stringValue = tag;
            tagManager.ApplyModifiedProperties();
        }

        private static void TryAddLayer(string layer)
        {
            if (string.IsNullOrEmpty(layer)) return;
            if (Enumerable.Range(0, 32).Select(LayerMask.LayerToName).Contains(layer)) return;
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath(ProjectTagManagerPath)[0]);
            var layersProp = tagManager.FindProperty("layers");
            for (int i = 8; i < layersProp.arraySize; i++)
            {
                var sp = layersProp.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(sp.stringValue))
                {
                    sp.stringValue = layer;
                    tagManager.ApplyModifiedProperties();
            return;
                }
            }
            Debug.LogWarning($"SpecBootstrapper: No free user layer slot to add '{layer}'.");
        }

        private static void TryAddSortingLayer(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath(ProjectTagManagerPath)[0]);
            var sortingLayers = tagManager.FindProperty("m_SortingLayers");
            for (int i = 0; i < sortingLayers.arraySize; i++)
            {
                var sp = sortingLayers.GetArrayElementAtIndex(i).FindPropertyRelative("name");
                if (sp != null && sp.stringValue == name) return;
            }
            sortingLayers.InsertArrayElementAtIndex(sortingLayers.arraySize);
            var elem = sortingLayers.GetArrayElementAtIndex(sortingLayers.arraySize - 1);
            elem.FindPropertyRelative("name").stringValue = name;
            elem.FindPropertyRelative("uniqueID").intValue = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            tagManager.ApplyModifiedProperties();
        }

        private static void SafeSetTag(GameObject go, string tag)
        {
            try { go.tag = tag; } catch { /* ignore if invalid */ }
        }

        private static bool TryParseColor(string hex, out Color color)
        {
            color = Color.black;
            if (string.IsNullOrEmpty(hex)) return false;
            if (hex.StartsWith("#")) hex = hex.Substring(1);
            if (ColorUtility.TryParseHtmlString("#" + hex, out color)) return true;
            return false;
        }

        private static TextAnchor ParseTextAnchor(string value)
        {
            if (string.IsNullOrEmpty(value)) return TextAnchor.UpperLeft;
            switch (value)
            {
                case "UpperLeft": return TextAnchor.UpperLeft;
                case "UpperCenter": return TextAnchor.UpperCenter;
                case "UpperRight": return TextAnchor.UpperRight;
                case "MiddleLeft": return TextAnchor.MiddleLeft;
                case "MiddleCenter": return TextAnchor.MiddleCenter;
                case "MiddleRight": return TextAnchor.MiddleRight;
                case "LowerLeft": return TextAnchor.LowerLeft;
                case "LowerCenter": return TextAnchor.LowerCenter;
                case "LowerRight": return TextAnchor.LowerRight;
                default: return TextAnchor.UpperLeft;
            }
        }

        // Helpers to read JSON structure
        private static Dictionary<string, object> GetDict(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            if (!d.TryGetValue(key, out var v)) return null;
            return v as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            return v.ToString();
        }

        private static float GetFloat(Dictionary<string, object> d, string key, float def)
        {
            var v = TryGetFloat(d, key);
            return v.HasValue ? v.Value : def;
        }

        private static float? TryGetFloat(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            if (v is float f) return f;
            if (v is double db) return (float)db;
            if (float.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
            return null;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            if (v is bool b) return b;
            if (bool.TryParse(v.ToString(), out var pb)) return pb;
            return def;
        }

        private static List<T> GetList<T>(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            if (!d.TryGetValue(key, out var v) || v == null) return null;
            var list = v as List<object>;
            if (list == null) return null;
            var result = new List<T>();
            foreach (var item in list)
            {
                if (item is T t) result.Add(t);
                else if (typeof(T) == typeof(string)) result.Add((T)(object)item.ToString());
                else if (typeof(T) == typeof(object)) result.Add((T)item);
            }
            return result;
        }

        private static Tuple<GameObject, Grid> GetOrCreateGrid()
        {
            var gridGO = GameObject.Find("Grid");
            if (gridGO == null)
            {
                gridGO = new GameObject("Grid");
            }
            var grid = gridGO.GetComponent<Grid>();
            if (grid == null)
            {
                try { grid = gridGO.AddComponent<Grid>(); }
                catch { /* ignore and create a fresh carrier */ }
            }
            if (grid == null)
            {
                var freshGO = new GameObject("Grid");
                grid = freshGO.AddComponent<Grid>();
                gridGO = freshGO;
            }
            return Tuple.Create(gridGO, grid);
        }
    }

    // Minimal JSON parser (BSD/MIT-like). Adapted from Unity's MiniJSON.
    internal static class MiniJSON
    {
        public static object Deserialize(string json)
        {
            if (json == null) return null;
            return Parser.Parse(json);
        }

        sealed class Parser : IDisposable
        {
            private const string WORD_BREAK = "{}[],:\"";
            private StringReader json;

            private Parser(string jsonString)
            {
                json = new StringReader(jsonString);
            }

            public static object Parse(string jsonString)
            {
                using (var instance = new Parser(jsonString))
                {
                    return instance.ParseValue();
                }
            }

            public void Dispose()
            {
                json.Dispose();
                json = null;
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();

                // consume '{'
                json.Read();
                int memberCount = 0;

                while (true)
                {
                    var nextToken = NextToken;
                    if (nextToken == TOKEN.CURLY_CLOSE)
                    {
                        return table;
                    }

                    // key
                    string name = ParseString();

                    // colon
                    if (NextToken != TOKEN.COLON)
                    {
        return null;
    }

                    // skip the colon
                    json.Read();

                    // value
                    table[name] = ParseValue();
                    memberCount++;
                    if (memberCount > 20000) return null;

                    switch (NextToken)
                    {
                        case TOKEN.COMMA:
                            json.Read();
                            continue;
                        case TOKEN.CURLY_CLOSE:
                            json.Read();
                            return table;
                        default:
                            return null;
                    }
                }
            }

            private List<object> ParseArray()
            {
                var array = new List<object>();

                // skip '['
                json.Read();
                int itemCount = 0;

                var parsing = true;
                while (parsing)
                {
                    var nextToken = NextToken;

                    switch (nextToken)
                    {
                        case TOKEN.NONE:
                            return null;
                        case TOKEN.SQUARE_CLOSE:
                            json.Read();
                            return array;
                        case TOKEN.COMMA:
                            json.Read();
                            break;
                        default:
                            var value = ParseValue();
                            array.Add(value);
                            itemCount++;
                            if (itemCount > 20000) return null;
                            break;
                    }
                }

                return array;
            }

            private object ParseValue()
            {
                switch (NextToken)
                {
                    case TOKEN.STRING:
                        return ParseString();
                    case TOKEN.NUMBER:
                        return ParseNumber();
                    case TOKEN.CURLY_OPEN:
                        return ParseObject();
                    case TOKEN.SQUARE_OPEN:
                        return ParseArray();
                    case TOKEN.TRUE:
                        return true;
                    case TOKEN.FALSE:
                        return false;
                    case TOKEN.NULL:
                        return null;
                    case TOKEN.NONE:
                        break;
                }

        return null;
    }

            private string ParseString()
            {
                var s = new System.Text.StringBuilder();
                char c;

                // skip opening '"'
                json.Read();

                var parsing = true;
                while (parsing)
                {
                    if (json.Peek() == -1)
                    {
                        parsing = false;
                        break;
                    }

                    c = NextChar;
                    if (c == '"')
                    {
                        parsing = false;
                        break;
                    }
                    else if (c == '\\')
                    {
                        if (json.Peek() == -1) { parsing = false; break; }
                        c = NextChar;
                        switch (c)
                        {
                            case '"':
                            case '\\':
                            case '/':
                                s.Append(c);
                                break;
                            case 'b': s.Append('\b'); break;
                            case 'f': s.Append('\f'); break;
                            case 'n': s.Append('\n'); break;
                            case 'r': s.Append('\r'); break;
                            case 't': s.Append('\t'); break;
                            case 'u':
                                var hex = new char[4];
                                for (int i = 0; i < 4; i++) hex[i] = NextChar;
                                s.Append((char)Convert.ToInt32(new string(hex), 16));
                                break;
                        }
                    }
                    else
                    {
                        s.Append(c);
                    }
                }

                return s.ToString();
            }

            private object ParseNumber()
            {
                string number = NextWord;

                if (number.IndexOf('.') != -1)
                {
                    if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble))
                        return parsedDouble;
                }
                else
                {
                    if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
                        return (double)parsedLong;
                }

                return 0;
            }

            private void EatWhitespace()
            {
                while (char.IsWhiteSpace(PeekChar)) json.Read();
            }

            private char PeekChar => Convert.ToChar(json.Peek());
            private char NextChar => Convert.ToChar(json.Read());
            private string NextWord
            {
                get
                {
                    var word = new System.Text.StringBuilder();
                    while (json.Peek() != -1 && WORD_BREAK.IndexOf(PeekChar) == -1)
                    {
                        word.Append(NextChar);
                    }
                    return word.ToString();
                }
            }

            private TOKEN NextToken
            {
                get
                {
                    EatWhitespace();
                    if (json.Peek() == -1) return TOKEN.NONE;

                    char c = PeekChar;
                    switch (c)
                    {
                        case '{': return TOKEN.CURLY_OPEN;
                        case '}': return TOKEN.CURLY_CLOSE;
                        case '[': return TOKEN.SQUARE_OPEN;
                        case ']': return TOKEN.SQUARE_CLOSE;
                        case ',': return TOKEN.COMMA;
                        case '"': return TOKEN.STRING;
                        case ':': return TOKEN.COLON;
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        case '-': return TOKEN.NUMBER;
                    }

                    string word = NextWord;
                    switch (word)
                    {
                        case "false": return TOKEN.FALSE;
                        case "true": return TOKEN.TRUE;
                        case "null": return TOKEN.NULL;
                    }

                    return TOKEN.NONE;
                }
            }

            private enum TOKEN
            {
                NONE,
                CURLY_OPEN,
                CURLY_CLOSE,
                SQUARE_OPEN,
                SQUARE_CLOSE,
                COLON,
                COMMA,
                STRING,
                NUMBER,
                TRUE,
                FALSE,
                NULL
            }
        }
    }
}


