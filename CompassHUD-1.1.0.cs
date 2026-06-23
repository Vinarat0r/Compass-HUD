using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using EFT.Interactive;
using UnityEngine;

[BepInPlugin("com.vinarators.compasshud", "Compass HUD", "1.1.0")]
public class CompassHUD : BaseUnityPlugin
{
    public enum MarkerType
    {
        Extraction,
        Transit,
        Quest
    }

    public class MarkerTarget
    {
        public Vector3 Position;
        public MarkerType Type;
        public float CurrentDistance;
        public string Name;
    }

    private GUIStyle compassStyle;
    private GUIStyle degreeStyle;
    private GUIStyle centralDegreeStyle;
    private GUIStyle distanceStyle;
    private Texture2D lineTexture;
    private Texture2D leftGradient;
    private Texture2D rightGradient;
    private Texture2D extractionTexture;
    private Texture2D questTexture;
    private System.Reflection.PropertyInfo gameWorldInstanceProperty;
    private System.Type transitType;
    private bool isInRaid;
    private float lastCheckTime;
    private float lastMarkerCheckTime;
    private float raidStartTime;
    private float cachedYaw;

    private ConfigEntry<bool> enabledCompass;
    private ConfigEntry<bool> showDegrees;
    private ConfigEntry<bool> showLetters;
    private ConfigEntry<float> scale;
    private ConfigEntry<float> opacity;
    private ConfigEntry<int> language;
    private ConfigEntry<KeyCode> toggleKey;
    private ConfigEntry<float> roundedness;
    private ConfigEntry<bool> showCentralIndicator;
    private ConfigEntry<bool> showExtractions;
    private ConfigEntry<bool> showTransits;
    private ConfigEntry<bool> showQuests;
    private ConfigEntry<bool> independentMarkerSize;
    private ConfigEntry<float> markerScale;

    private List<MarkerTarget> activeMarkers = new List<MarkerTarget>();
    private List<ExfiltrationPoint> cachedExfils = new List<ExfiltrationPoint>();
    private List<Component> cachedTransits = new List<Component>();
    private List<Component> cachedQuestTargets = new List<Component>();
    private List<float> currentFrameMarkerX = new List<float>();
    private bool markersInitialized;
    private Player mainPlayer;

    private void Awake()
    {
        enabledCompass = Config.Bind("General", "Enabled", true, "Enable Compass HUD");
        showDegrees = Config.Bind("General", "Show Degrees", true, "Show heading degrees on tape");
        showLetters = Config.Bind("General", "Show Letters", true, "Show heading letters on tape");
        language = Config.Bind("General", "Language", 0, "0 = Auto, 1 = English, 2 = Russian");
        scale = Config.Bind("Appearance", "Scale", 1.0f, "Compass scale");
        opacity = Config.Bind("Appearance", "Opacity", 0.9f, "Compass opacity");
        toggleKey = Config.Bind("General", "Toggle Hotkey", KeyCode.F8, "Button to toggle compass visibility");
        roundedness = Config.Bind("Appearance", "Roundedness", 1.0f, "How strongly the sides fade out for 3D effect");
        showCentralIndicator = Config.Bind("General", "Show Central Indicator", true, "Show the central indicator box");
        showExtractions = Config.Bind("UI Markers", "Show Extractions", false, "Show extraction markers on compass");
        showTransits = Config.Bind("UI Markers", "Show Transits", false, "Show map transit points on compass");
        showQuests = Config.Bind("UI Markers", "Show Quests", false, "Show quest targets on compass");
        independentMarkerSize = Config.Bind("UI Markers", "Independent Marker Size", false, "Disable distance-based scaling for markers");
        markerScale = Config.Bind("UI Markers", "Marker Scale", 1.21f, "Scale factor for markers and icons");

        compassStyle = new GUIStyle();
        compassStyle.fontStyle = FontStyle.Bold;
        compassStyle.alignment = TextAnchor.MiddleCenter;
        compassStyle.normal.textColor = Color.white;

        degreeStyle = new GUIStyle();
        degreeStyle.fontStyle = FontStyle.Normal;
        degreeStyle.alignment = TextAnchor.MiddleCenter;
        degreeStyle.normal.textColor = Color.white;

        centralDegreeStyle = new GUIStyle();
        centralDegreeStyle.fontStyle = FontStyle.Bold;
        centralDegreeStyle.alignment = TextAnchor.MiddleCenter;
        centralDegreeStyle.normal.textColor = Color.white;

        distanceStyle = new GUIStyle();
        distanceStyle.fontStyle = FontStyle.Bold;
        distanceStyle.alignment = TextAnchor.MiddleCenter;
        distanceStyle.normal = new GUIStyleState { textColor = Color.white };

        lineTexture = new Texture2D(1, 1);
        lineTexture.SetPixel(0, 0, Color.white);
        lineTexture.Apply();

        leftGradient = new Texture2D(32, 1);
        rightGradient = new Texture2D(32, 1);
        for (int i = 0; i < 32; i++)
        {
            float t = (float)i / 31f;
            leftGradient.SetPixel(i, 0, new Color(0f, 0f, 0f, 0.18f * t));
            rightGradient.SetPixel(i, 0, new Color(0f, 0f, 0f, 0.18f * (1f - t)));
        }
        leftGradient.Apply();
        rightGradient.Apply();

        extractionTexture = LoadTextureFromFile("Standard_27.png");
        questTexture = LoadTextureFromFile("icon_quest.png");

        try
        {
            System.Type singletonType = System.Type.GetType("Comfort.Common.Singleton`1, Comfort");
            System.Type gameWorldType = System.Type.GetType("EFT.GameWorld, Assembly-CSharp");
            if (singletonType != null && gameWorldType != null)
            {
                gameWorldInstanceProperty = singletonType.MakeGenericType(gameWorldType).GetProperty("Instance");
            }
            transitType = System.Type.GetType("EFT.Interactive.TransitPoint, Assembly-CSharp");
        }
        catch
        {
            gameWorldInstanceProperty = null;
            transitType = null;
        }
    }

    private Texture2D LoadTextureFromFile(string filename)
    {
        string[] possiblePaths = new string[]
        {
            Path.Combine(BepInEx.Paths.GameRootPath, "SPT", "SPT_Data"),
            Path.Combine(BepInEx.Paths.GameRootPath, "SPT_Data"),
            Path.Combine(BepInEx.Paths.GameRootPath, "..", "SPT", "SPT_Data")
        };

        foreach (var basePath in possiblePaths)
        {
            if (!Directory.Exists(basePath)) continue;

            string[] files = Directory.GetFiles(basePath, filename, SearchOption.AllDirectories);
            if (files.Length > 0)
            {
                byte[] fileData = File.ReadAllBytes(files[0]);
                Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (UnityEngine.ImageConversion.LoadImage(texture, fileData))
                {
                    return texture;
                }
            }
        }
        return null;
    }

    private void Update()
    {
        if (Time.time - lastCheckTime > 0.5f)
        {
            lastCheckTime = Time.time;
            if (gameWorldInstanceProperty != null)
            {
                try
                {
                    var instance = gameWorldInstanceProperty.GetValue(null, null);
                    isInRaid = instance != null;
                }
                catch
                {
                    isInRaid = false;
                }
            }
            else
            {
                var gw = GameObject.Find("GameWorld");
                isInRaid = gw != null;
            }

            if (!isInRaid)
            {
                mainPlayer = null;
                markersInitialized = false;
                cachedExfils.Clear();
                cachedTransits.Clear();
                cachedQuestTargets.Clear();
                activeMarkers.Clear();
            }
            else if (mainPlayer == null)
            {
                try
                {
                    if (gameWorldInstanceProperty != null)
                    {
                        var gwInstance = gameWorldInstanceProperty.GetValue(null, null);
                        if (gwInstance != null)
                        {
                            var mainPlayerProp = gwInstance.GetType().GetProperty("MainPlayer") ?? gwInstance.GetType().GetProperty("YourPlayer");
                            if (mainPlayerProp != null)
                            {
                                mainPlayer = mainPlayerProp.GetValue(gwInstance, null) as Player;
                            }
                        }
                    }
                }
                catch { }

                if (mainPlayer == null)
                {
                    mainPlayer = GameObject.FindObjectsOfType<Player>().FirstOrDefault(p => p.IsYourPlayer);
                }

                if (mainPlayer != null)
                {
                    raidStartTime = Time.time;
                }
            }
        }

        if (!isInRaid || Cursor.visible)
        {
            return;
        }

        if (mainPlayer != null && !markersInitialized && (Time.time - raidStartTime > 4.0f))
        {
            cachedExfils = GetEligibleExfils();

            cachedTransits.Clear();
            if (transitType != null)
            {
                var transits = GameObject.FindObjectsOfType(transitType);
                foreach (var t in transits)
                {
                    if (t is Component comp) cachedTransits.Add(comp);
                }
            }

            cachedQuestTargets.Clear();
            if (showQuests.Value)
            {
                var placeItemTriggerType = System.Type.GetType("EFT.Interactive.PlaceItemTrigger, Assembly-CSharp");
                if (placeItemTriggerType != null)
                {
                    var triggers = GameObject.FindObjectsOfType(placeItemTriggerType);
                    foreach (var t in triggers)
                    {
                        if (t is Component comp) cachedQuestTargets.Add(comp);
                    }
                }

                var questTriggerType = System.Type.GetType("EFT.Interactive.QuestTrigger, Assembly-CSharp");
                if (questTriggerType != null)
                {
                    var triggers = GameObject.FindObjectsOfType(questTriggerType);
                    foreach (var t in triggers)
                    {
                        if (t is Component comp) cachedQuestTargets.Add(comp);
                    }
                }

                var questZoneType = System.Type.GetType("EFT.Interactive.QuestZone, Assembly-CSharp");
                if (questZoneType != null)
                {
                    var zones = GameObject.FindObjectsOfType(questZoneType);
                    foreach (var z in zones)
                    {
                        if (z is Component comp) cachedQuestTargets.Add(comp);
                    }
                }

                var lootItemType = System.Type.GetType("EFT.Interactive.LootItem, Assembly-CSharp");
                if (lootItemType != null)
                {
                    var items = GameObject.FindObjectsOfType(lootItemType);
                    foreach (var t in items)
                    {
                        if (t is Component comp && IsQuestLootItem(comp))
                        {
                            cachedQuestTargets.Add(comp);
                        }
                    }
                }
            }

            markersInitialized = true;
            RefreshActiveTargets();
        }

        if (UnityEngine.Input.GetKeyDown(toggleKey.Value))
        {
            enabledCompass.Value = !enabledCompass.Value;
        }

        if (!enabledCompass.Value)
            return;

        Camera cam = Camera.current;
        if (cam != null)
            cachedYaw = cam.transform.eulerAngles.y;

        if (Time.time - lastMarkerCheckTime > 1.0f)
        {
            lastMarkerCheckTime = Time.time;
            RefreshActiveTargets();
        }
    }

    private string LocalizeAndCleanName(string rawName, string fallbackSearchText = "", bool isTransit = false)
    {
        if (string.IsNullOrEmpty(rawName)) rawName = fallbackSearchText;
        if (string.IsNullOrEmpty(rawName)) return "Exit";
        bool ru = "Exit".Localized().ToLower().Contains("выход");

        string lower = rawName.ToLower() + " " + fallbackSearchText.ToLower();
        if (isTransit || lower.Contains("transit") || lower.Contains("transfer") || lower.Contains("to_"))
        {
            if (lower.Contains("interchange") || lower.Contains("razvjazka")) return ru ? "Переход: Развязка" : "Transit: Interchange";
            if (lower.Contains("bigmap") || lower.Contains("customs") || lower.Contains("tamozhna")) return ru ? "Переход: Таможня" : "Transit: Customs";
            if (lower.Contains("shoreline") || lower.Contains("bereg")) return ru ? "Переход: Берег" : "Transit: Shoreline";
            if (lower.Contains("lighthouse") || lower.Contains("majak")) return ru ? "Переход: Маяк" : "Transit: Lighthouse";
            if (lower.Contains("rezerv") || lower.Contains("reserve")) return ru ? "Переход: Резерв" : "Transit: Reserve";
            if (lower.Contains("woods") || lower.Contains("les")) return ru ? "Переход: Лес" : "Transit: Woods";
            if (lower.Contains("town") || lower.Contains("streets") || lower.Contains("ulici") || lower.Contains("streets of tarkov")) return ru ? "Переход: Улицы Таркова" : "Transit: Streets";
            if (lower.Contains("sandbox") || lower.Contains("groundzero") || lower.Contains("epicentr") || lower.Contains("ground zero") || lower.Contains("epi")) return ru ? "Переход: Эпицентр" : "Transit: Ground Zero";
            if (lower.Contains("factory") || lower.Contains("zavod")) return ru ? "Переход: Завод" : "Transit: Factory";
            if (lower.Contains("terminal")) return ru ? "Переход: Терминал" : "Transit: Terminal";
            if (lower.Contains("lab")) return ru ? "Переход: Лаборатория" : "Transit: Laboratory";
            return ru ? "Переход" : "Transit Point";
        }

        try
        {
            string localized = rawName.Localized();
            if (!string.IsNullOrEmpty(localized) && localized != rawName)
            {
                return localized;
            }

            string upperLocalized = rawName.ToUpper().Localized();
            if (!string.IsNullOrEmpty(upperLocalized) && upperLocalized != rawName.ToUpper())
            {
                return upperLocalized;
            }
        }
        catch { }

        if (lower.Contains("unity_free_exit")) return ru ? "Вентиляция" : "Ventilation Shaft";
        if (lower.Contains("groundzero_secret_adaptation")) return ru ? "Кафе / Накаты" : "Scientist Taproom";
        if (lower.Contains("scav_exit")) return ru ? "Выход для Дикого" : "Scav Exit";
        if (lower.Contains("pmc_exit")) return ru ? "Выход для ЧВК" : "PMC Exit";

        string[] parts = rawName.Split(new char[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        List<string> cleanParts = new List<string>();

        foreach (string part in parts)
        {
            string lowerPart = part.ToLower();
            if (lowerPart == "exit" || lowerPart == "adaptation" || lowerPart == "pmc" || lowerPart == "scav")
            {
                continue;
            }

            if (part.Length > 0)
            {
                cleanParts.Add(char.ToUpper(part[0]) + part.Substring(1));
            }
        }

        string result = string.Join(" ", cleanParts);
        return string.IsNullOrEmpty(result) ? rawName : result;
    }

    private bool IsQuestLootItem(Component lootItem)
    {
        if (lootItem == null) return false;
        try
        {
            var type = lootItem.GetType();
            var itemProp = type.GetProperty("Item") ?? type.GetProperty("item");
            if (itemProp == null) return false;
            var item = itemProp.GetValue(lootItem, null);
            if (item == null) return false;

            var questItemProp = item.GetType().GetProperty("QuestItem") ?? item.GetType().GetProperty("questItem");
            if (questItemProp != null)
            {
                return (bool)questItemProp.GetValue(item, null);
            }

            var templateProp = item.GetType().GetProperty("Template") ?? item.GetType().GetProperty("template");
            if (templateProp != null)
            {
                var template = templateProp.GetValue(item, null);
                if (template != null)
                {
                    var templateType = template.GetType();
                    var prop = templateType.GetProperty("QuestItem") ?? templateType.GetProperty("questItem");
                    if (prop != null)
                    {
                        return (bool)prop.GetValue(template, null);
                    }
                    var field = templateType.GetField("QuestItem") ?? templateType.GetField("questItem");
                    if (field != null)
                    {
                        return (bool)field.GetValue(template);
                    }
                }
            }
        }
        catch { }
        return false;
    }

    private string GetQuestLootItemName(Component lootItem)
    {
        if (lootItem == null) return "Quest Item";
        try
        {
            var type = lootItem.GetType();
            var itemProp = type.GetProperty("Item") ?? type.GetProperty("item");
            if (itemProp != null)
            {
                var item = itemProp.GetValue(lootItem, null);
                if (item != null)
                {
                    var nameProp = item.GetType().GetProperty("Name") ?? item.GetType().GetProperty("name");
                    if (nameProp != null)
                    {
                        string rawName = nameProp.GetValue(item, null)?.ToString();
                        if (!string.IsNullOrEmpty(rawName))
                        {
                            return rawName.Localized();
                        }
                    }

                    var shortNameProp = item.GetType().GetProperty("ShortName") ?? item.GetType().GetProperty("shortName");
                    if (shortNameProp != null)
                    {
                        string rawShort = shortNameProp.GetValue(item, null)?.ToString();
                        if (!string.IsNullOrEmpty(rawShort))
                        {
                            return rawShort.Localized();
                        }
                    }
                }
            }
        }
        catch { }
        return "Quest Item";
    }

    private string GetZoneId(Component trigger)
    {
        if (trigger == null) return null;
        var type = trigger.GetType();
        var zoneIdField = type.GetField("ZoneId") ?? type.GetField("zoneId") ?? type.GetField("TriggerId") ?? type.GetField("triggerId");
        if (zoneIdField != null)
        {
            return zoneIdField.GetValue(trigger)?.ToString();
        }

        var zoneIdProp = type.GetProperty("ZoneId") ?? type.GetProperty("zoneId") ?? type.GetProperty("TriggerId") ?? type.GetProperty("triggerId");
        if (zoneIdProp != null)
        {
            return zoneIdProp.GetValue(trigger, null)?.ToString();
        }

        return trigger.gameObject.name;
    }

    private List<ExfiltrationPoint> GetEligibleExfils()
    {
        var result = new List<ExfiltrationPoint>();
        if (gameWorldInstanceProperty == null || mainPlayer == null || mainPlayer.Profile == null) return result;

        try
        {
            var gw = gameWorldInstanceProperty.GetValue(null, null);
            if (gw != null)
            {
                var controllerProp = gw.GetType().GetProperty("ExfiltrationController") ?? gw.GetType().GetProperty("exfiltrationController");
                var controller = controllerProp != null ? controllerProp.GetValue(gw, null) : null;

                if (controller == null)
                {
                    var controllerField = gw.GetType().GetField("ExfiltrationController") ?? gw.GetType().GetField("exfiltrationController") ?? gw.GetType().GetField("_exfiltrationController");
                    if (controllerField != null)
                    {
                        controller = controllerField.GetValue(gw);
                    }
                }

                if (controller != null)
                {
                    var eligiblePointsMethod = controller.GetType().GetMethod("EligiblePoints", new System.Type[] { typeof(Profile) })
                        ?? controller.GetType().GetMethod("GetEligiblePoints", new System.Type[] { typeof(Profile) });

                    if (eligiblePointsMethod != null)
                    {
                        var points = eligiblePointsMethod.Invoke(controller, new object[] { mainPlayer.Profile }) as ExfiltrationPoint[];
                        if (points != null)
                        {
                            result.AddRange(points);
                        }
                    }
                }
            }
        }
        catch { }

        if (result.Count == 0)
        {
            try
            {
                var allExfils = GameObject.FindObjectsOfType<ExfiltrationPoint>();
                string localEntryPoint = mainPlayer.Profile?.Info?.EntryPoint?.ToLower();
                bool isScav = mainPlayer.Profile != null && mainPlayer.Profile.Side == EPlayerSide.Savage;

                foreach (var point in allExfils)
                {
                    if (point == null || !point.gameObject.activeInHierarchy) continue;

                    bool isScavPoint = point is ScavExfiltrationPoint;
                    if (isScav != isScavPoint) continue;

                    string statusStr = point.Status.ToString();
                    if (statusStr == "NotPresent" || statusStr == "Unknown") continue;

                    bool isEligible = true;
                    var eligibleMethod = point.GetType().GetMethod("EligibleForUser", new System.Type[] { typeof(Player) });
                    if (eligibleMethod != null)
                    {
                        isEligible = (bool)eligibleMethod.Invoke(point, new object[] { mainPlayer });
                    }
                    if (!isEligible) continue;

                    if (!string.IsNullOrEmpty(localEntryPoint) && point.EligibleEntryPoints != null && point.EligibleEntryPoints.Length > 0)
                    {
                        bool matches = false;
                        foreach (string ep in point.EligibleEntryPoints)
                        {
                            if (ep != null && ep.ToLower() == localEntryPoint)
                            {
                                matches = true;
                                break;
                            }
                        }
                        if (!matches) continue;
                    }

                    result.Add(point);
                }
            }
            catch { }
        }

        return result;
    }

    private void RefreshActiveTargets()
    {
        activeMarkers.Clear();
        if (!isInRaid || mainPlayer == null || !markersInitialized) return;

        if (showExtractions.Value)
        {
            foreach (var point in cachedExfils)
            {
                if (point != null)
                {
                    string nameLower = (point.Settings?.Name ?? "").ToLower();
                    if (nameLower.Contains("secret") || nameLower.Contains("adaptation") || nameLower.Contains("test") || nameLower.Contains("note") || nameLower.Contains("scout"))
                    {
                        continue;
                    }

                    string statusStr = point.Status.ToString();
                    if (statusStr == "NotPresent" || statusStr == "Unknown")
                    {
                        continue;
                    }

                    activeMarkers.Add(new MarkerTarget
                    {
                        Position = point.transform.position,
                        Type = MarkerType.Extraction,
                        Name = LocalizeAndCleanName(point.Settings?.Name)
                    });
                }
            }
        }

        if (showTransits.Value)
        {
            foreach (var comp in cachedTransits)
            {
                if (comp != null && comp.gameObject.activeInHierarchy)
                {
                    string tName = "Transit";
                    try
                    {
                        var proxyField = comp.GetType().GetField("TransitProperties") ?? comp.GetType().GetField("Properties");
                        if (proxyField != null)
                        {
                            var props = proxyField.GetValue(comp);
                            if (props != null)
                            {
                                var pInfo = props.GetType().GetProperty("Name");
                                if (pInfo != null)
                                {
                                    var nVal = pInfo.GetValue(props, null);
                                    if (nVal != null) tName = nVal.ToString();
                                }
                                else
                                {
                                    var fInfo = props.GetType().GetField("Name");
                                    if (fInfo != null)
                                    {
                                        var nVal = fInfo.GetValue(props);
                                        if (nVal != null) tName = nVal.ToString();
                                    }
                                }
                            }
                        }
                    }
                    catch { }

                    activeMarkers.Add(new MarkerTarget
                    {
                        Position = comp.transform.position,
                        Type = MarkerType.Transit,
                        Name = LocalizeAndCleanName(tName, comp.gameObject.name, true)
                    });
                }
            }
        }

        if (showQuests.Value)
        {
            foreach (var comp in cachedQuestTargets)
            {
                if (comp != null && comp.gameObject.activeInHierarchy)
                {
                    string qName = "Quest Objective";
                    bool isLoot = IsQuestLootItem(comp);

                    if (isLoot)
                    {
                        qName = GetQuestLootItemName(comp);
                    }
                    else
                    {
                        string zoneId = GetZoneId(comp);
                        if (!string.IsNullOrEmpty(zoneId))
                        {
                            string localized = zoneId.Localized();
                            if (!string.IsNullOrEmpty(localized) && localized != zoneId)
                            {
                                qName = localized;
                            }
                            else
                            {
                                continue;
                            }
                        }
                        else
                        {
                            continue;
                        }
                    }

                    activeMarkers.Add(new MarkerTarget
                    {
                        Position = comp.transform.position,
                        Type = MarkerType.Quest,
                        Name = qName
                    });
                }
            }
        }

        for (int i = activeMarkers.Count - 1; i >= 0; i--)
        {
            if (activeMarkers[i].Type == MarkerType.Transit)
            {
                bool nearExfil = false;
                for (int j = 0; j < activeMarkers.Count; j++)
                {
                    if (i != j && activeMarkers[j].Type == MarkerType.Extraction && Vector3.Distance(activeMarkers[i].Position, activeMarkers[j].Position) < 5f)
                    {
                        nearExfil = true;
                        break;
                    }
                }
                if (nearExfil)
                {
                    activeMarkers.RemoveAt(i);
                }
            }
        }

        for (int i = activeMarkers.Count - 1; i >= 0; i--)
        {
            bool overlap = false;
            for (int j = 0; j < i; j++)
            {
                if (activeMarkers[i].Type == activeMarkers[j].Type && Vector3.Distance(activeMarkers[i].Position, activeMarkers[j].Position) < 2f)
                {
                    overlap = true;
                    break;
                }
            }
            if (overlap)
            {
                activeMarkers.RemoveAt(i);
            }
        }
    }

    private void OnGUI()
    {
        if (!enabledCompass.Value || !isInRaid || Cursor.visible)
            return;

        if (Camera.current == null)
            return;

        float centerX = Screen.width / 2f;
        float topY = 6f;
        float width = 900f * scale.Value;
        bool markersActive = showExtractions.Value || showTransits.Value || showQuests.Value;
        float yOffset = (markersActive ? 4f : 8f) * scale.Value;
        float height = (markersActive ? 75f : 56f) * scale.Value;
        float centralY = topY + (markersActive ? 14f : 8f) * scale.Value;

        Color oldColor = GUI.color;

        DrawLinearGradientBackground(centerX, width, topY, height);

        GUI.color = new Color(1f, 1f, 1f, opacity.Value);

        MarkerTarget closestCenterTarget = null;
        float closestDeltaAngle = float.MaxValue;
        float normYaw = Normalize(cachedYaw);

        currentFrameMarkerX.Clear();

        if (mainPlayer != null)
        {
            float pixelsPerDegree = (width / 80f);
            Vector3 pPos = mainPlayer.Transform.position;

            for (int i = 0; i < activeMarkers.Count; i++)
            {
                MarkerTarget target = activeMarkers[i];
                Vector3 dir = target.Position - pPos;
                float ang = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                if (ang < 0) ang += 360f;

                float da = Mathf.DeltaAngle(normYaw, ang);
                if (Mathf.Abs(da) < closestDeltaAngle)
                {
                    closestDeltaAngle = Mathf.Abs(da);
                    if (closestDeltaAngle < 3.5f)
                    {
                        closestCenterTarget = target;
                        closestCenterTarget.CurrentDistance = Vector3.Distance(pPos, target.Position);
                    }
                }
            }

            for (int i = 0; i < activeMarkers.Count; i++)
            {
                MarkerTarget target = activeMarkers[i];
                Vector3 dir = target.Position - pPos;
                float ang = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                if (ang < 0) ang += 360f;

                float da = Mathf.DeltaAngle(normYaw, ang);
                if (Mathf.Abs(da) <= 40f)
                {
                    bool isCentered = (target == closestCenterTarget && closestDeltaAngle < 3.5f);
                    float finalX = isCentered ? centerX : (centerX + da * pixelsPerDegree);
                    currentFrameMarkerX.Add(finalX);
                }
            }
        }

        if (showCentralIndicator.Value)
        {
            string centralText = "";
            bool ru = language.Value == 2 || (language.Value == 0 && "Exit".Localized().ToLower().Contains("выход"));

            if (closestCenterTarget != null && closestDeltaAngle < 3.5f)
            {
                centralText = "     ";
            }
            else
            {
                float[] majorAngles = { 0f, 45f, 90f, 135f, 180f, 225f, 270f, 315f };
                string[] majorNamesEn = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
                string[] majorNamesRu = { "С", "СВ", "В", "ЮВ", "Ю", "ЮЗ", "З", "СЗ" };

                bool isPointingAtLetter = false;
                for (int i = 0; i < majorAngles.Length; i++)
                {
                    if (Mathf.Abs(Mathf.DeltaAngle(normYaw, majorAngles[i])) <= 3.5f)
                    {
                        centralText = ru ? majorNamesRu[i] : majorNamesEn[i];
                        isPointingAtLetter = true;
                        break;
                    }
                }

                if (!isPointingAtLetter)
                    centralText = ((int)normYaw).ToString();
            }

            centralDegreeStyle.fontSize = Mathf.RoundToInt(26f * scale.Value);
            string centralDegree = "[ " + centralText + " ]";
            GUI.Label(new Rect(centerX - 150f * scale.Value, centralY, 300f * scale.Value, 40f * scale.Value), centralDegree, centralDegreeStyle);
        }

        DrawCompassLinear(centerX, topY, width, height, normYaw, yOffset);

        if (mainPlayer != null)
        {
            float halfWidth = width / 2f;
            float pixelsPerDegree = (width / 80f);
            Vector3 playerPos = mainPlayer.Transform.position;

            for (int i = 0; i < activeMarkers.Count; i++)
            {
                MarkerTarget target = activeMarkers[i];
                Texture2D tex = (target.Type == MarkerType.Quest) ? (questTexture ?? extractionTexture) : extractionTexture;
                if (tex == null) continue;

                Vector3 dirToTarget = target.Position - playerPos;
                float angleToTarget = Mathf.Atan2(dirToTarget.x, dirToTarget.z) * Mathf.Rad2Deg;
                if (angleToTarget < 0) angleToTarget += 360f;

                float deltaAngle = Mathf.DeltaAngle(normYaw, angleToTarget);

                if (Mathf.Abs(deltaAngle) > 40f)
                    continue;

                bool isCentered = (target == closestCenterTarget && closestDeltaAngle < 3.5f);
                float x = isCentered ? centerX : (centerX + deltaAngle * pixelsPerDegree);

                if (x < centerX - halfWidth || x > centerX + halfWidth)
                    continue;

                float normDist = isCentered ? 0f : (Mathf.Abs(x - centerX) / halfWidth);
                float smoothPower = Mathf.Lerp(20f, 0.4f, Mathf.Clamp(roundedness.Value, 0f, 10f) / 10f);
                float smoothFactor = Mathf.Clamp01(1f - Mathf.Pow(normDist, smoothPower));
                float elementAlpha = smoothFactor * opacity.Value;

                if (target.Type == MarkerType.Extraction)
                    GUI.color = new Color(0.95f, 0.7f, 0f, elementAlpha);
                else if (target.Type == MarkerType.Transit)
                    GUI.color = new Color(1.0f, 0.15f, 0.15f, elementAlpha);
                else
                    GUI.color = new Color(0f, 0.95f, 0.15f, elementAlpha);

                float distance = Vector3.Distance(playerPos, target.Position);
                float clampedDistance = Mathf.Clamp(distance, 10f, 500f);

                float dynamicScale = 1.0f;
                if (isCentered)
                    dynamicScale = 1.4f;
                else if (!independentMarkerSize.Value)
                    dynamicScale = Mathf.Lerp(1.4f, 0.7f, (clampedDistance - 10f) / (500f - 10f));

                dynamicScale *= scale.Value * markerScale.Value;

                float iconSize = 40f * dynamicScale;
                if (target.Type == MarkerType.Quest)
                {
                    iconSize = 24f * dynamicScale;
                }

                float iconY = (topY + 34f * scale.Value) - (iconSize / 2f);
                GUI.DrawTexture(new Rect(x - iconSize / 2f, iconY, iconSize, iconSize), tex);

                string subDistStr = Mathf.RoundToInt(distance) + "m";
                GUIStyle subStyle = new GUIStyle(distanceStyle);
                subStyle.fontSize = Mathf.RoundToInt(10f * dynamicScale);
                subStyle.normal = new GUIStyleState { textColor = GUI.color };
                GUI.Label(new Rect(x - 50f * scale.Value, iconY + iconSize + 1f, 100f * scale.Value, 16f * scale.Value), subDistStr, subStyle);
            }
        }

        GUI.color = new Color(1f, 1f, 1f, opacity.Value);

        if (closestCenterTarget != null)
        {
            GUIStyle bottomStyle = new GUIStyle(distanceStyle);
            bottomStyle.fontSize = Mathf.RoundToInt(16f * scale.Value);
            Color labelColor = Color.white;

            if (closestCenterTarget.Type == MarkerType.Transit)
                labelColor = new Color(1.0f, 0.15f, 0.15f, opacity.Value);
            else if (closestCenterTarget.Type == MarkerType.Extraction)
                labelColor = new Color(0.95f, 0.7f, 0f, opacity.Value);
            else
                labelColor = new Color(0f, 0.95f, 0.15f, opacity.Value);

            bottomStyle.normal = new GUIStyleState { textColor = labelColor };

            string distStr = closestCenterTarget.Name + " [" + Mathf.RoundToInt(closestCenterTarget.CurrentDistance) + "m]";
            GUI.Label(new Rect(centerX - 250f, topY + height + 4f, 500f, 20f * scale.Value), distStr, bottomStyle);
        }

        GUI.color = oldColor;
    }

    private void DrawLinearGradientBackground(float centerX, float width, float topY, float height)
    {
        float halfWidth = width / 2f;
        float gradWidth = 50f * scale.Value;
        Color originalColor = GUI.color;

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(centerX - halfWidth, topY, gradWidth, height), leftGradient);

        GUI.color = new Color(0f, 0f, 0f, 0.18f);
        GUI.DrawTexture(new Rect(centerX - halfWidth + gradWidth, topY, width - 2f * gradWidth, height), Texture2D.whiteTexture);

        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(centerX + halfWidth - gradWidth, topY, gradWidth, height), rightGradient);

        GUI.color = originalColor;
    }

    private void DrawCompassLinear(float centerX, float topY, float width, float height, float yaw, float yOffset)
    {
        float halfWidth = width / 2f;
        float pixelsPerDegree = (width / 80f);

        for (int angle = 0; angle < 360; angle += 5)
        {
            float relativeAngle = Mathf.DeltaAngle(yaw, angle);

            if (Mathf.Abs(relativeAngle) > 40f)
                continue;

            float x = centerX + relativeAngle * pixelsPerDegree;

            if (x < centerX - halfWidth || x > centerX + halfWidth)
                continue;

            if (showCentralIndicator.Value && Mathf.Abs(relativeAngle) < 4.0f)
                continue;

            bool isOverlappingMarker = false;
            foreach (float mx in currentFrameMarkerX)
            {
                if (Mathf.Abs(x - mx) < 22f * scale.Value)
                {
                    isOverlappingMarker = true;
                    break;
                }
            }

            float normDist = Mathf.Abs(x - centerX) / halfWidth;
            float smoothPower = Mathf.Lerp(20f, 0.4f, Mathf.Clamp(roundedness.Value, 0f, 10f) / 10f);
            float smoothFactor = Mathf.Clamp01(1f - Mathf.Pow(normDist, smoothPower));
            float elementAlpha = smoothFactor * opacity.Value;

            GUI.color = new Color(1f, 1f, 1f, elementAlpha);

            bool major = angle % 45 == 0;
            bool medium = angle % 15 == 0;

            float lineWidth = 1.5f * scale.Value;
            float lineHeight;

            if (major)
                lineHeight = 14.0f * smoothFactor * scale.Value;
            else if (medium)
                lineHeight = 9.0f * smoothFactor * scale.Value;
            else
                lineHeight = 6.0f * smoothFactor * scale.Value;

            if (!isOverlappingMarker)
            {
                GUI.DrawTexture(new Rect(x - lineWidth / 2f, topY + yOffset, lineWidth, lineHeight), lineTexture);
            }

            float textY = topY + yOffset + lineHeight + 2f * scale.Value;

            if (isOverlappingMarker)
                continue;

            if (major)
            {
                if (showLetters.Value)
                {
                    string dir = GetDirection(angle);
                    if (dir != "")
                    {
                        compassStyle.fontSize = Mathf.RoundToInt(22f * smoothFactor * scale.Value);
                        GUI.Label(new Rect(x - 30f * scale.Value, textY, 60f * scale.Value, 24f * scale.Value), dir, compassStyle);
                    }
                }
                else if (showDegrees.Value)
                {
                    degreeStyle.fontSize = Mathf.RoundToInt(14f * smoothFactor * scale.Value);
                    GUI.Label(new Rect(x - 30f * scale.Value, textY, 60f * scale.Value, 28f * scale.Value), angle.ToString(), degreeStyle);
                }
            }
            else if (medium && showDegrees.Value)
            {
                degreeStyle.fontSize = Mathf.RoundToInt(14f * smoothFactor * scale.Value);
                GUI.Label(new Rect(x - 30f * scale.Value, textY, 60f * scale.Value, 20f * scale.Value), angle.ToString(), degreeStyle);
            }
        }
    }

    private string GetDirection(float angle)
    {
        bool ru = language.Value == 2 || (language.Value == 0 && "Exit".Localized().ToLower().Contains("выход"));

        if (angle >= 337.5f || angle < 22.5f) return ru ? "С" : "N";
        if (angle >= 22.5f && angle < 67.5f) return ru ? "СВ" : "NE";
        if (angle >= 67.5f && angle < 112.5f) return ru ? "В" : "E";
        if (angle >= 112.5f && angle < 157.5f) return ru ? "ЮВ" : "SE";
        if (angle >= 157.5f && angle < 202.5f) return ru ? "Ю" : "S";
        if (angle >= 202.5f && angle < 247.5f) return ru ? "ЮЗ" : "SW";
        if (angle >= 247.5f && angle < 292.5f) return ru ? "З" : "W";
        if (angle >= 292.5f && angle < 337.5f) return ru ? "СЗ" : "NW";

        return "";
    }

    private float Normalize(float value)
    {
        while (value < 0) value += 360f;
        while (value >= 360f) value -= 360f;
        return value;
    }
}