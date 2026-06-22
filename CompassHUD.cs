using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

[BepInPlugin("com.vinarators.compasshud", "Compass HUD", "1.0.0")]
public class CompassHUD : BaseUnityPlugin
{
    private GUIStyle compassStyle;
    private GUIStyle degreeStyle;
    private GUIStyle centralDegreeStyle;
    private Texture2D lineTexture;
    private Texture2D leftGradient;
    private Texture2D rightGradient;
    private System.Reflection.PropertyInfo gameWorldInstanceProperty;
    private bool isInRaid;
    private float lastCheckTime;
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

    private void Awake()
    {
        enabledCompass = Config.Bind("General", "Enabled", true, "Enable Compass HUD");
        showDegrees = Config.Bind("General", "Show Degrees", true, "Show heading degrees on tape");
        showLetters = Config.Bind("General", "Show Letters", true, "Show heading letters on tape");
        language = Config.Bind("General", "Language", 0, "0 = English, 1 = Russian");
        scale = Config.Bind("Appearance", "Scale", 1.0f, "Compass scale");
        opacity = Config.Bind("Appearance", "Opacity", 0.9f, "Compass opacity");
        toggleKey = Config.Bind("General", "Toggle Hotkey", KeyCode.F8, "Button to toggle compass visibility");
        roundedness = Config.Bind("Appearance", "Roundedness", 1.0f, "How strongly the sides fade out for 3D effect");
        showCentralIndicator = Config.Bind("General", "Show Central Indicator", true, "Show the central indicator box");

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

        try
        {
            System.Type singletonType = System.Type.GetType("Comfort.Common.Singleton`1, Comfort");
            System.Type gameWorldType = System.Type.GetType("EFT.GameWorld, Assembly-CSharp");
            if (singletonType != null && gameWorldType != null)
            {
                gameWorldInstanceProperty = singletonType.MakeGenericType(gameWorldType).GetProperty("Instance");
            }
        }
        catch
        {
            gameWorldInstanceProperty = null;
        }
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
                    isInRaid = gameWorldInstanceProperty.GetValue(null, null) != null;
                }
                catch
                {
                    isInRaid = false;
                }
            }
            else
            {
                isInRaid = GameObject.Find("GameWorld") != null;
            }
        }

        if (!isInRaid || Cursor.visible)
        {
            return;
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
        float height = 60f * scale.Value;

        Color oldColor = GUI.color;

        DrawLinearGradientBackground(centerX, width, topY, height);

        GUI.color = new Color(1f, 1f, 1f, opacity.Value);

        if (showCentralIndicator.Value)
        {
            float normYaw = Normalize(cachedYaw);
            string centralText = "";
            bool ru = language.Value == 1;

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
            {
                centralText = ((int)normYaw).ToString();
            }

            centralDegreeStyle.fontSize = Mathf.RoundToInt(26f * scale.Value);
            string centralDegree = "[ " + centralText + " ]";
            GUI.Label(new Rect(centerX - 150f * scale.Value, topY, 300f * scale.Value, height), centralDegree, centralDegreeStyle);
        }

        DrawCompassLinear(centerX, topY, width, height, cachedYaw);

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

    private void DrawCompassLinear(float centerX, float topY, float width, float height, float yaw)
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

            GUI.DrawTexture(new Rect(x - lineWidth / 2f, topY + 6f * scale.Value, lineWidth, lineHeight), lineTexture);

            float textY = topY + 24f * scale.Value;

            if (major)
            {
                if (showLetters.Value)
                {
                    string dir = GetDirection(angle);
                    if (dir != "")
                    {
                        compassStyle.fontSize = Mathf.RoundToInt(22f * smoothFactor * scale.Value);
                        GUI.Label(new Rect(x - 30f * scale.Value, textY, 60f * scale.Value, 28f * scale.Value), dir, compassStyle);
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
                GUI.Label(new Rect(x - 30f * scale.Value, textY, 60f * scale.Value, 28f * scale.Value), angle.ToString(), degreeStyle);
            }
        }
    }

    private string GetDirection(float angle)
    {
        bool ru = language.Value == 1;

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