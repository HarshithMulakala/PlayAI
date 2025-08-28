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

        [MenuItem("Tools/AI/Import From Spec...")]
        private static void ImportFromSpecMenu()
        {
            string specPath = EditorUtility.OpenFilePanel("Select Game Spec JSON", Application.dataPath, "json");
            if (string.IsNullOrEmpty(specPath)) return;

            string scriptsFolder = EditorUtility.OpenFolderPanel("Select Scripts Folder (output/scripts)", Application.dataPath, "");
            if (string.IsNullOrEmpty(scriptsFolder)) return;

            PrepareScriptsAndQueueImport(specPath, scriptsFolder);
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
                string json = File.ReadAllText(specPath);
                var root = MiniJSON.Deserialize(json) as Dictionary<string, object>;
                if (root == null)
                {
                    Debug.LogError("SpecBootstrapper: Failed to parse spec JSON root.");
                    return;
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
                    if (Enum.TryParse<RenderMode>(renderModeStr.Replace("ScreenSpace", "ScreenSpace"), out var rm))
                    {
                        canvas.renderMode = rm;
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
                    if (type == "Box")
                    {
                        var box = go.AddComponent<BoxCollider2D>();
                        var size = GetDict(colDict, "size");
                        if (size != null)
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
                    }
                    else if (type == "Circle")
                    {
                        var circle = go.AddComponent<CircleCollider2D>();
                        float? r = TryGetFloat(colDict, "radius");
                        if (r.HasValue)
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
                    }
                    else if (type == "Polygon")
                    {
                        go.AddComponent<PolygonCollider2D>();
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
                if (anchorMin != null) rect.anchorMin = new Vector2(GetFloat(anchorMin, "x", 0f), GetFloat(anchorMin, "y", 0f));
                if (anchorMax != null) rect.anchorMax = new Vector2(GetFloat(anchorMax, "x", 1f), GetFloat(anchorMax, "y", 1f));
                if (pivot != null) rect.pivot = new Vector2(GetFloat(pivot, "x", 0.5f), GetFloat(pivot, "y", 0.5f));
                if (position != null) rect.anchoredPosition = new Vector2(GetFloat(position, "x", 0f), GetFloat(position, "y", 0f));
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
                }
                AttachUIScripts(go, ui);
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
            // Try to resolve type by name across loaded assemblies
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(scriptName))
                .FirstOrDefault(t => t != null && typeof(MonoBehaviour).IsAssignableFrom(t));

            if (type == null)
            {
                // Try with namespace-qualified guess if exists in default assembly
                type = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => a.GetTypes())
                    .FirstOrDefault(t => t.Name == scriptName && typeof(MonoBehaviour).IsAssignableFrom(t));
            }

            if (type == null)
            {
                Debug.LogWarning($"SpecBootstrapper: Could not find script type '{scriptName}'. Ensure it compiled successfully.");
            return;
        }

            var mb = go.AddComponent(type);
            if (mb == null || parameters == null || parameters.Count == 0) return;

            // Map parameters to public fields/properties by name
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
                        object converted = ConvertParameterValue(value, field.FieldType);
                        field.SetValue(mb, converted);
                    }
                    else if (prop != null && prop.CanWrite)
                    {
                        object converted = ConvertParameterValue(value, prop.PropertyType);
                        prop.SetValue(mb, converted);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"SpecBootstrapper: Failed to set parameter '{fieldName}' on '{scriptName}': {ex.Message}");
                }
            }
        }

        private static object ConvertParameterValue(object value, Type targetType)
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
            return value;
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

            if (pixelsPerUnit.HasValue)
            {
                var importer = AssetImporter.GetAtPath(assetsRel) as TextureImporter;
                if (importer != null)
                {
                    bool needsReimport = false;
                    if (importer.textureType != TextureImporterType.Sprite)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                        needsReimport = true;
                    }
                    if (Mathf.Abs(importer.spritePixelsPerUnit - pixelsPerUnit.Value) > 0.001f)
                    {
                        importer.spritePixelsPerUnit = pixelsPerUnit.Value;
                        needsReimport = true;
                    }
                    if (needsReimport)
                    {
                        importer.SaveAndReimport();
                    }
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


