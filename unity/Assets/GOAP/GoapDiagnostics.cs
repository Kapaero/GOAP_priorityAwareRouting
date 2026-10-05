using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class GoapDiagnostics
{
    const int MaxLinesPerFrame = 300;
    const int FlushEveryLines = 60;
    const float FlushEverySeconds = 1f;

    static readonly Dictionary<string, int> lastLogFrameByKey = new Dictionary<string, int>();

    static StreamWriter writer;
    static bool initialized;
    static bool registeredQuitHandler;
    static int currentFrame = -1;
    static int linesThisFrame;
    static int droppedLinesThisFrame;
    static int totalDroppedLines;
    static int linesSinceFlush;
    static float lastFlushTime;

    public static bool Enabled = true;
    // Set by headless campaign builds. Wins over Enabled, which the scene's WorldStateChangeDebugger
    // switches back on every frame.
    public static bool ForcedOff;
    public static string LogPath { get; private set; }

    public static bool IsActive
    {
        get { return Enabled && !ForcedOff; }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        Close();
        initialized = false;
        registeredQuitHandler = false;
        currentFrame = -1;
        linesThisFrame = 0;
        droppedLinesThisFrame = 0;
        totalDroppedLines = 0;
        linesSinceFlush = 0;
        lastFlushTime = 0f;
        lastLogFrameByKey.Clear();
        Enabled = true;
        ForcedOff = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void InitializeBeforeSceneLoad()
    {
        Initialize();
    }

    public static void Log(string category, string message)
    {
        if (!IsActive)
            return;

        RefreshFrameCounters();

        if (linesThisFrame >= MaxLinesPerFrame)
        {
            droppedLinesThisFrame++;
            totalDroppedLines++;
            return;
        }

        if (!Initialize())
            return;

        try
        {
            writer.WriteLine(FormatLine(category, message));
            linesThisFrame++;
            linesSinceFlush++;
            FlushIfNeeded();
        }
        catch (Exception exception)
        {
            Enabled = false;
            Debug.LogError("GOAP diagnostics disabled after write error: " + exception.Message);
        }
    }

    public static void LogOncePerFrame(string key, string category, string message)
    {
        LogThrottled(key, 1, category, message);
    }

    public static void LogThrottled(string key, int intervalFrames, string category, string message)
    {
        if (!IsActive)
            return;

        int frame = Time.frameCount;
        if (lastLogFrameByKey.TryGetValue(key, out int lastFrame)
            && frame - lastFrame < Mathf.Max(1, intervalFrames))
        {
            return;
        }

        lastLogFrameByKey[key] = frame;
        Log(category, message);
    }

    public static void Flush()
    {
        if (writer == null)
            return;

        try
        {
            writer.Flush();
            linesSinceFlush = 0;
            lastFlushTime = Time.realtimeSinceStartup;
        }
        catch (Exception exception)
        {
            Enabled = false;
            Debug.LogError("GOAP diagnostics disabled after flush error: " + exception.Message);
        }
    }

    public static void Close()
    {
        if (writer == null)
            return;

        try
        {
            writer.WriteLine(FormatLine("Diagnostics", "session closed. totalDroppedLines=" + totalDroppedLines));
            writer.Flush();
            writer.Close();
        }
        catch
        {
        }
        finally
        {
            writer = null;
            initialized = false;
        }
    }

    static bool Initialize()
    {
        if (initialized)
            return writer != null;

        initialized = true;

        try
        {
            LogPath = ResolveLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            writer = new StreamWriter(LogPath, false);
            writer.WriteLine(FormatLine("Diagnostics", "session started. logPath=" + LogPath));
            lastFlushTime = Time.realtimeSinceStartup;

            if (!registeredQuitHandler)
            {
                Application.quitting += Close;
                registeredQuitHandler = true;
            }

            return true;
        }
        catch (Exception exception)
        {
            Enabled = false;
            Debug.LogError("GOAP diagnostics could not open log file: " + exception.Message);
            return false;
        }
    }

    static string ResolveLogPath()
    {
        string root;
#if UNITY_EDITOR
        root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#else
        root = Application.persistentDataPath;
#endif
        return Path.Combine(root, "GOAP_Diagnostics", "goap_diagnostics_latest.log");
    }

    static void RefreshFrameCounters()
    {
        int frame = Time.frameCount;
        if (currentFrame == frame)
            return;

        if (droppedLinesThisFrame > 0)
        {
            int dropped = droppedLinesThisFrame;
            droppedLinesThisFrame = 0;
            currentFrame = frame;
            linesThisFrame = 0;
            Log("Diagnostics", "dropped lines in previous frame=" + dropped + ", totalDroppedLines=" + totalDroppedLines);
            return;
        }

        currentFrame = frame;
        linesThisFrame = 0;
    }

    static string FormatLine(string category, string message)
    {
        return "frame="
            + Time.frameCount
            + " time="
            + Time.realtimeSinceStartup.ToString("F3", CultureInfo.InvariantCulture)
            + " dt="
            + Time.unscaledDeltaTime.ToString("F4", CultureInfo.InvariantCulture)
            + " category="
            + Sanitize(category)
            + " :: "
            + Sanitize(message);
    }

    static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "<empty>";

        return value.Replace('\r', ' ').Replace('\n', ' ');
    }

    static void FlushIfNeeded()
    {
        if (linesSinceFlush < FlushEveryLines
            && Time.realtimeSinceStartup - lastFlushTime < FlushEverySeconds)
        {
            return;
        }

        Flush();
    }
}
