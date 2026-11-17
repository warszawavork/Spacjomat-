using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;

public class AiConfig
{
    public bool EnableAi = true;
    public string Provider = "gemini"; // "gemini" or "openai"
    public string ApiKey = "";
    public string Model = "gemini-3.1-flash-lite";
    public string CustomEndpoint = "";
    public string Style = "natural"; // "natural", "business", "strict"
    public string CorrectionMode = "suggest"; // "suggest" or "auto"
    public bool FastHedgeMode = true; // Szybka asekuracja F7 (rownolegly zapasowy model przy zwisie >1.9s)
    public int DailyLimitEstimate = 250;
    public int RpmLimitEstimate = 15;
    public bool ShowPill = true;
    public string PillSizePreset = "medium"; // "compact", "medium", "large", "custom"
    public int PillWidth = 265;
    public int PillHeight = 44;
    public int PillOpacity = 95;
    public int PillX = -1;
    public int PillY = -1;

    public static string GetConfigPath()
    {
        return Path.Combine(SpacjomatContext.GetAppDir(), "ai_config.json");
    }

    public void Clamp()
    {
        if (string.IsNullOrEmpty(Provider)) Provider = "gemini";
        if (string.IsNullOrEmpty(Model)) Model = "gemini-3.1-flash-lite";
        if (string.IsNullOrEmpty(Style)) Style = "natural";
        if (CorrectionMode != "auto") CorrectionMode = "suggest";
        if (DailyLimitEstimate < 20) DailyLimitEstimate = 20;
        if (DailyLimitEstimate > 100000) DailyLimitEstimate = 100000;
        if (RpmLimitEstimate < 1) RpmLimitEstimate = 1;
        if (RpmLimitEstimate > 5000) RpmLimitEstimate = 5000;
        if (string.IsNullOrEmpty(PillSizePreset)) PillSizePreset = "medium";
        if (PillWidth < 160) PillWidth = 160;
        if (PillWidth > 620) PillWidth = 620;
        if (PillHeight < 28) PillHeight = 28;
        if (PillHeight > 110) PillHeight = 110;
        if (PillOpacity < 45) PillOpacity = 45;
        if (PillOpacity > 100) PillOpacity = 100;
        if (ApiKey != null) ApiKey = SanitizeKey(ApiKey);
    }

    public static string SanitizeKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        return raw.Trim().Trim('"', '\'', ' ', '\r', '\n', '\t');
    }

    public void CopyAiFieldsFrom(AiConfig src)
    {
        if (src == null) return;
        EnableAi = src.EnableAi;
        Provider = src.Provider;
        ApiKey = src.ApiKey;
        Model = src.Model;
        CustomEndpoint = src.CustomEndpoint;
        Style = src.Style;
        CorrectionMode = src.CorrectionMode;
        FastHedgeMode = src.FastHedgeMode;
        DailyLimitEstimate = src.DailyLimitEstimate;
        RpmLimitEstimate = src.RpmLimitEstimate;
        Clamp();
    }

    public static AiConfig Load()
    {
        try
        {
            string p = GetConfigPath();
            if (File.Exists(p))
            {
                string json = File.ReadAllText(p, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var cfg = ser.Deserialize<AiConfig>(json);
                if (cfg != null)
                {
                    cfg.Clamp();
                    return cfg;
                }
            }
        }
        catch { }
        return new AiConfig();
    }

    public void Save()
    {
        try
        {
            Clamp();
            var ser = new JavaScriptSerializer();
            string json = ser.Serialize(this);
            File.WriteAllText(GetConfigPath(), json, new UTF8Encoding(false));
        }
        catch { }
    }
}

public class AiUsageData
{
    public string DayKey = "";
    public int RequestsToday = 0;
    public int SuccessToday = 0;
    public int ErrorsToday = 0;
    public int RateLimits429Today = 0;
    public long PromptTokensToday = 0;
    public long OutputTokensToday = 0;
    public long TotalTokensToday = 0;
    public Dictionary<string, int> ModelRequestsToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Model429Today = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    public bool HasRemoteQuota = false;
    public string RemoteReqLimit = "";
    public string RemoteReqRemaining = "";
    public string RemoteReqReset = "";
    public string RemoteTokLimit = "";
    public string RemoteTokRemaining = "";
    public string LastModelUsed = "";
    public string LastStatusText = "";
    public string LastUpdatedTime = "";
}

public static class AiUsageTracker
{
    private static readonly object syncRoot = new object();
    private static AiUsageData data = null;
    private static readonly List<DateTime> recentReqUtc = new List<DateTime>();
    private static readonly List<KeyValuePair<DateTime, int>> recentTokUtc = new List<KeyValuePair<DateTime, int>>();

    private static string GetUsagePath()
    {
        return Path.Combine(SpacjomatContext.GetAppDir(), "ai_usage.json");
    }

    private static string CurrentPacificDayKey()
    {
        // Google AI Studio resets daily RPD quotas around midnight Pacific Time (UTC-8)
        return DateTime.UtcNow.AddHours(-8).ToString("yyyy-MM-dd");
    }

    private static void EnsureLoadedLocked()
    {
        if (data == null)
        {
            try
            {
                string p = GetUsagePath();
                if (File.Exists(p))
                {
                    string json = File.ReadAllText(p, Encoding.UTF8);
                    var ser = new JavaScriptSerializer();
                    data = ser.Deserialize<AiUsageData>(json);
                }
            }
            catch { }
            if (data == null) data = new AiUsageData();
            if (data.ModelRequestsToday == null) data.ModelRequestsToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (data.Model429Today == null) data.Model429Today = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        string todayKey = CurrentPacificDayKey();
        if (data.DayKey != todayKey)
        {
            data.DayKey = todayKey;
            data.RequestsToday = 0;
            data.SuccessToday = 0;
            data.ErrorsToday = 0;
            data.RateLimits429Today = 0;
            data.PromptTokensToday = 0;
            data.OutputTokensToday = 0;
            data.TotalTokensToday = 0;
            data.ModelRequestsToday = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            data.Model429Today = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveLocked()
    {
        try
        {
            var ser = new JavaScriptSerializer();
            File.WriteAllText(GetUsagePath(), ser.Serialize(data), new UTF8Encoding(false));
        }
        catch { }
    }

    private static void PruneMinuteWindowLocked(DateTime nowUtc)
    {
        DateTime cutoff = nowUtc.AddSeconds(-60);
        for (int i = recentReqUtc.Count - 1; i >= 0; i--)
        {
            if (recentReqUtc[i] < cutoff) recentReqUtc.RemoveAt(i);
        }
        for (int i = recentTokUtc.Count - 1; i >= 0; i--)
        {
            if (recentTokUtc[i].Key < cutoff) recentTokUtc.RemoveAt(i);
        }
    }

    public static void RecordResponse(string provider, string model, bool success, int httpCode, int promptTok, int outTok, int totalTok, WebHeaderCollection respHeaders, string errText)
    {
        lock (syncRoot)
        {
            EnsureLoadedLocked();
            DateTime nowUtc = DateTime.UtcNow;
            PruneMinuteWindowLocked(nowUtc);

            data.RequestsToday++;
            if (success) data.SuccessToday++;
            else data.ErrorsToday++;
            if (httpCode == 429) data.RateLimits429Today++;

            if (!string.IsNullOrEmpty(model))
            {
                data.LastModelUsed = model;
                int prevCount = 0;
                data.ModelRequestsToday.TryGetValue(model, out prevCount);
                data.ModelRequestsToday[model] = prevCount + 1;
                if (httpCode == 429)
                {
                    int prev429 = 0;
                    data.Model429Today.TryGetValue(model, out prev429);
                    data.Model429Today[model] = prev429 + 1;
                }
            }

            if (totalTok <= 0 && (promptTok > 0 || outTok > 0)) totalTok = promptTok + outTok;
            if (promptTok > 0) data.PromptTokensToday += promptTok;
            if (outTok > 0) data.OutputTokensToday += outTok;
            if (totalTok > 0)
            {
                data.TotalTokensToday += totalTok;
                recentTokUtc.Add(new KeyValuePair<DateTime, int>(nowUtc, totalTok));
            }
            recentReqUtc.Add(nowUtc);

            data.LastUpdatedTime = DateTime.Now.ToString("HH:mm:ss");
            if (success)
            {
                data.LastStatusText = string.Format("OK ({0})", model ?? provider ?? "AI");
            }
            else if (httpCode == 429)
            {
                data.LastStatusText = "HTTP 429 (Limit osiągnięty!)";
            }
            else
            {
                string shortErr = errText ?? ("HTTP " + httpCode);
                if (shortErr.Length > 90) shortErr = shortErr.Substring(0, 87) + "...";
                data.LastStatusText = shortErr;
            }

            if (respHeaders != null)
            {
                string remReq = GetHeaderIgnoreCase(respHeaders, "x-ratelimit-remaining-requests");
                string limReq = GetHeaderIgnoreCase(respHeaders, "x-ratelimit-limit-requests");
                string rstReq = GetHeaderIgnoreCase(respHeaders, "x-ratelimit-reset-requests");
                string remTok = GetHeaderIgnoreCase(respHeaders, "x-ratelimit-remaining-tokens");
                string limTok = GetHeaderIgnoreCase(respHeaders, "x-ratelimit-limit-tokens");
                if (!string.IsNullOrEmpty(remReq) || !string.IsNullOrEmpty(limReq) || !string.IsNullOrEmpty(remTok) || !string.IsNullOrEmpty(limTok))
                {
                    data.HasRemoteQuota = true;
                    data.RemoteReqRemaining = remReq ?? "";
                    data.RemoteReqLimit = limReq ?? "";
                    data.RemoteReqReset = rstReq ?? "";
                    data.RemoteTokRemaining = remTok ?? "";
                    data.RemoteTokLimit = limTok ?? "";
                }
                else if (provider == "gemini")
                {
                    data.HasRemoteQuota = false;
                }
            }

            SaveLocked();
        }
    }

    private static string GetHeaderIgnoreCase(WebHeaderCollection headers, string target)
    {
        if (headers == null) return null;
        foreach (string k in headers.AllKeys)
        {
            if (k != null && k.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return headers[k];
            }
        }
        return null;
    }

    public static void Reset()
    {
        lock (syncRoot)
        {
            data = new AiUsageData();
            data.DayKey = CurrentPacificDayKey();
            recentReqUtc.Clear();
            recentTokUtc.Clear();
            SaveLocked();
        }
    }

    public static AiUsageData GetSnapshot(out int currentRpm, out int currentTpm)
    {
        lock (syncRoot)
        {
            EnsureLoadedLocked();
            DateTime nowUtc = DateTime.UtcNow;
            PruneMinuteWindowLocked(nowUtc);
            currentRpm = recentReqUtc.Count;
            int t = 0;
            foreach (var kv in recentTokUtc) t += kv.Value;
            currentTpm = t;

            var copy = new AiUsageData();
            copy.DayKey = data.DayKey;
            copy.RequestsToday = data.RequestsToday;
            copy.SuccessToday = data.SuccessToday;
            copy.ErrorsToday = data.ErrorsToday;
            copy.RateLimits429Today = data.RateLimits429Today;
            copy.PromptTokensToday = data.PromptTokensToday;
            copy.OutputTokensToday = data.OutputTokensToday;
            copy.TotalTokensToday = data.TotalTokensToday;
            copy.ModelRequestsToday = new Dictionary<string, int>(data.ModelRequestsToday, StringComparer.OrdinalIgnoreCase);
            copy.Model429Today = new Dictionary<string, int>(data.Model429Today, StringComparer.OrdinalIgnoreCase);
            copy.HasRemoteQuota = data.HasRemoteQuota;
            copy.RemoteReqLimit = data.RemoteReqLimit;
            copy.RemoteReqRemaining = data.RemoteReqRemaining;
            copy.RemoteReqReset = data.RemoteReqReset;
            copy.RemoteTokLimit = data.RemoteTokLimit;
            copy.RemoteTokRemaining = data.RemoteTokRemaining;
            copy.LastModelUsed = data.LastModelUsed;
            copy.LastStatusText = data.LastStatusText;
            copy.LastUpdatedTime = data.LastUpdatedTime;
            return copy;
        }
    }
}

public class AiSuggestionOption
{
    public string Label = "";
    public string Text = "";
}

public class AiSuggestionResult
{
    public bool HasErrors = false;
    public List<string> Fixes = new List<string>();
    public List<AiSuggestionOption> Options = new List<AiSuggestionOption>();
    public string ModelUsed = "";
    public bool IsOffline = false;
}

public static class AiCorrectionEngine
{
    private class CacheEntry
    {
        public string Value;
        public DateTime ExpiresUtc;
    }

    private static readonly object cacheLock = new object();
    private static readonly Dictionary<string, CacheEntry> aiCache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
    private static readonly LinkedList<string> aiCacheOrder = new LinkedList<string>();

    // Zapamietuje dla kazdego modelu dzialajacy tryb thinkingConfig ("budget0", "minimal", "none")
    private static readonly Dictionary<string, string> thinkingModeCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly object metaLock = new object();
    private static List<string> cachedGeminiModels = null;
    private static string cachedGeminiKey = "";
    private static DateTime cachedGeminiModelsExpiry = DateTime.MinValue;
    private static int lastWarmUpTick = 0;

    static AiCorrectionEngine()
    {
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.DefaultConnectionLimit = 24;
            ServicePointManager.MaxServicePointIdleTime = 180000;
            ServicePointManager.DnsRefreshTimeout = 300000;
        }
        catch { }
    }

    public static void WarmUpConnectionAsync(AiConfig cfg)
    {
        if (cfg == null || !IsAiReady(cfg)) return;
        lastWarmUpTick = Environment.TickCount;
        ThreadPool.QueueUserWorkItem(state =>
        {
            try
            {
                string cleanKey = AiConfig.SanitizeKey(cfg.ApiKey);
                if (cfg.Provider == "gemini" && !string.IsNullOrEmpty(cleanKey))
                {
                    var headers = new Dictionary<string, string>();
                    headers["x-goog-api-key"] = cleanKey;
                    WebHeaderCollection hdrs;
                    HttpWebRequest reqOut;
                    HttpRequestWithMeta("GET", "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1", null, headers, 4500, out hdrs, out reqOut);
                }
                else if (cfg.Provider == "openai")
                {
                    string ep = string.IsNullOrEmpty(cfg.CustomEndpoint) ? "https://api.openai.com/v1/chat/completions" : cfg.CustomEndpoint.Trim();
                    int idx = ep.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase);
                    string modelsUrl = (idx > 0) ? (ep.Substring(0, idx) + "/models") : ep;
                    var headers = new Dictionary<string, string>();
                    if (!string.IsNullOrEmpty(cleanKey)) headers["Authorization"] = "Bearer " + cleanKey;
                    WebHeaderCollection hdrs;
                    HttpWebRequest reqOut;
                    HttpRequestWithMeta("GET", modelsUrl, null, headers, 4500, out hdrs, out reqOut);
                }
            }
            catch { }
        });
    }

    public static void MaybeKeepAliveAsync(AiConfig cfg)
    {
        if (cfg == null || !IsAiReady(cfg)) return;
        int now = Environment.TickCount;
        if (unchecked(now - lastWarmUpTick) < 55000) return;
        WarmUpConnectionAsync(cfg);
    }

    public static bool IsAiReady(AiConfig cfg)
    {
        if (cfg == null || !cfg.EnableAi) return false;
        string cleanKey = AiConfig.SanitizeKey(cfg.ApiKey);
        if (!string.IsNullOrEmpty(cleanKey) && cleanKey.Length > 5) return true;
        if (cfg.Provider == "openai" && !string.IsNullOrEmpty(cfg.CustomEndpoint) && cfg.CustomEndpoint.Contains("localhost")) return true;
        return false;
    }

    private static string BuildSystemPrompt(string style, bool jsonSuggestMode, int inputLen)
    {
        string styleNote = "Zachowaj naturalny styl.";
        if (style == "business")
        {
            styleNote = "Nadaj uprzejmy ton biznesowy.";
        }
        else if (style == "strict")
        {
            styleNote = "Zmieniaj minimalnie — tylko błędy.";
        }

        if (!jsonSuggestMode)
        {
            return "Szybki korektor PL. " + styleNote +
                   " Popraw literówki, polskie znaki, błędne słowa i interpunkcję w CAŁYM tekście bez skracania. Zachowaj URL i liczby. Zwróć WYŁĄCZNIE poprawiony tekst.";
        }

        if (style == "strict" || inputLen > 350)
        {
            return "Szybki korektor PL. " + styleNote +
                   " Popraw literówki, polskie znaki, błędne słowa i interpunkcję w CAŁYM tekście bez skracania. " +
                   "Zwróć TYLKO czysty JSON: {\"hasErrors\":true,\"f\":[\"błąd->poprawka\"],\"o\":[{\"l\":\"✨ Poprawnie\",\"t\":\"cały poprawiony tekst\"}]}. " +
                   "W f max 3 wyrazy, w o 1 opcja. Brak błędów: {\"hasErrors\":false,\"f\":[],\"o\":[]}.";
        }

        return "Szybki korektor PL. " + styleNote +
               " Popraw literówki, polskie znaki, błędne słowa i interpunkcję w CAŁYM tekście bez skracania. " +
               "Zwróć TYLKO czysty JSON: {\"hasErrors\":true,\"f\":[\"błąd->poprawka\"],\"o\":[{\"l\":\"✨ Poprawnie\",\"t\":\"cały poprawiony tekst\"},{\"l\":\"💬 Krócej/inaczej\",\"t\":\"opcjonalny 2. wariant\"}]}. " +
               "W f max 3 wyrazy, w o max 2 opcje. Brak błędów: {\"hasErrors\":false,\"f\":[],\"o\":[]}.";
    }

    public static List<string> DiscoverGeminiModels(string apiKey, out string err)
    {
        return DiscoverGeminiModels(apiKey, false, out err);
    }

    public static List<string> DiscoverGeminiModels(string apiKey, bool forceRefresh, out string err)
    {
        err = null;
        string cleanKey = AiConfig.SanitizeKey(apiKey);
        if (!forceRefresh)
        {
            lock (metaLock)
            {
                if (cachedGeminiModels != null && cachedGeminiModels.Count > 0 &&
                    cachedGeminiKey == cleanKey && DateTime.UtcNow < cachedGeminiModelsExpiry)
                {
                    return new List<string>(cachedGeminiModels);
                }
            }
        }

        var found = new List<string>();
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            var headers = new Dictionary<string, string>();
            headers["x-goog-api-key"] = cleanKey;

            WebHeaderCollection respHeaders;
            HttpWebRequest reqOut;
            string json = HttpRequestWithMeta("GET", "https://generativelanguage.googleapis.com/v1beta/models?pageSize=100", null, headers, 7000, out respHeaders, out reqOut);
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 10485760;
            var parsed = ser.Deserialize<Dictionary<string, object>>(json);
            if (parsed != null && parsed.ContainsKey("models"))
            {
                var list = parsed["models"] as System.Collections.ArrayList;
                if (list != null)
                {
                    foreach (object item in list)
                    {
                        var m = item as Dictionary<string, object>;
                        if (m == null || !m.ContainsKey("name")) continue;
                        string name = ((string)m["name"]).Replace("models/", "");
                        if (name.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (name.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("imagen", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("veo", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("tts", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("robotics", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (name.IndexOf("live", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                        bool supportsGen = false;
                        if (m.ContainsKey("supportedGenerationMethods"))
                        {
                            var methods = m["supportedGenerationMethods"] as System.Collections.ArrayList;
                            if (methods != null)
                            {
                                foreach (object meth in methods)
                                {
                                    if ("generateContent".Equals(meth as string, StringComparison.OrdinalIgnoreCase))
                                    {
                                        supportsGen = true;
                                        break;
                                    }
                                }
                            }
                        }
                        if (supportsGen) found.Add(name);
                    }
                }
            }

            found.Sort((a, b) => ScoreGeminiModel(b).CompareTo(ScoreGeminiModel(a)));
            if (found.Count > 0)
            {
                lock (metaLock)
                {
                    cachedGeminiModels = new List<string>(found);
                    cachedGeminiKey = cleanKey;
                    cachedGeminiModelsExpiry = DateTime.UtcNow.AddMinutes(30);
                }
            }
        }
        catch (WebException wex)
        {
            int code;
            err = ParseHttpError(wex, out code);
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        return found;
    }

    private static int ScoreGeminiModel(string name)
    {
        string l = name.ToLower();
        int score = 0;
        if (l.Contains("flash")) score += 500;
        if (l.Contains("lite")) score += 320;
        if (l.Contains("3.1-flash-lite") && !l.Contains("preview")) score += 160;
        if (l.Contains("3.5-flash-lite")) score += 135;
        if (l.Contains("flash-lite-latest")) score += 120;
        if (l.Contains("3.1")) score += 95;
        if (l.Contains("3.")) score += 70;
        if (l.Contains("2.5")) score += 30;
        if (l.Contains("2.0")) score += 20;
        if (l.Contains("latest") && !l.Contains("lite")) score -= 140;
        if (l.Contains("pro") || l.Contains("deep-research")) score -= 200;
        if (l.Contains("exp") || l.Contains("thinking") || l.Contains("preview") || l.Contains("image") || l.Contains("nano") || l.Contains("lyria") || l.Contains("transcribe") || l.Contains("computer-use")) score -= 300;
        return score;
    }

    private static string ResolveThinkingMode(string model)
    {
        string m = (model ?? "").ToLowerInvariant();
        lock (metaLock)
        {
            string cached;
            if (thinkingModeCache.TryGetValue(m, out cached)) return cached;
        }
        if (m.Contains("2.0") || m.Contains("image") || m.Contains("tts")) return "none";
        if (m.Contains("3.5") || m.Contains("3.6") || m.Contains("3.7") || m.Contains("3.8") || m.Contains("omni") || m.Contains("flash-lite-latest") || m.Contains("flash-latest"))
        {
            return "minimal";
        }
        if (m.Contains("3.1") || m.Contains("2.5") || m.Contains("3-flash"))
        {
            return "budget0";
        }
        return "none";
    }

    private static void RememberThinkingMode(string model, string mode)
    {
        if (string.IsNullOrEmpty(model)) return;
        lock (metaLock)
        {
            thinkingModeCache[model.ToLowerInvariant()] = mode;
        }
    }

    private static string GetCachedAi(string cacheKey)
    {
        lock (cacheLock)
        {
            CacheEntry entry;
            if (aiCache.TryGetValue(cacheKey, out entry))
            {
                if (DateTime.UtcNow < entry.ExpiresUtc) return entry.Value;
                aiCache.Remove(cacheKey);
            }
        }
        return null;
    }

    private static void PutCachedAi(string cacheKey, string val)
    {
        if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(val)) return;
        lock (cacheLock)
        {
            if (aiCache.ContainsKey(cacheKey))
            {
                aiCache[cacheKey] = new CacheEntry { Value = val, ExpiresUtc = DateTime.UtcNow.AddMinutes(30) };
                return;
            }
            if (aiCacheOrder.Count >= 250)
            {
                string oldest = aiCacheOrder.First.Value;
                aiCacheOrder.RemoveFirst();
                aiCache.Remove(oldest);
            }
            aiCache[cacheKey] = new CacheEntry { Value = val, ExpiresUtc = DateTime.UtcNow.AddMinutes(30) };
            aiCacheOrder.AddLast(cacheKey);
        }
    }

    private static string ParseHttpError(WebException wex, out int httpCode)
    {
        httpCode = 0;
        try
        {
            if (wex.Response != null)
            {
                var httpResp = wex.Response as HttpWebResponse;
                if (httpResp != null) httpCode = (int)httpResp.StatusCode;

                using (var sr = new StreamReader(wex.Response.GetResponseStream(), Encoding.UTF8))
                {
                    string raw = sr.ReadToEnd();
                    try
                    {
                        var ser = new JavaScriptSerializer();
                        var obj = ser.Deserialize<Dictionary<string, object>>(raw);
                        if (obj != null && obj.ContainsKey("error"))
                        {
                            var errObj = obj["error"] as Dictionary<string, object>;
                            if (errObj != null)
                            {
                                string msg = errObj.ContainsKey("message") ? (string)errObj["message"] : "";
                                string st = errObj.ContainsKey("status") ? (string)errObj["status"] : "";
                                if (errObj.ContainsKey("code") && httpCode == 0)
                                {
                                    int.TryParse(errObj["code"].ToString(), out httpCode);
                                }
                                return string.Format("HTTP {0} ({1}): {2}", httpCode, st, msg);
                            }
                        }
                    }
                    catch { }
                    return string.Format("HTTP {0}: {1}", httpCode, raw.Replace("\r", " ").Replace("\n", " "));
                }
            }
        }
        catch { }
        return wex.Message;
    }

    private static int ReadInt(Dictionary<string, object> dict, string key)
    {
        if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return 0;
        int val = 0;
        int.TryParse(dict[key].ToString(), out val);
        return val;
    }

    public static string CorrectWithAi(string inputText, AiConfig cfg, out string errorMsg, out long elapsedMs)
    {
        string usedModel;
        int len = (inputText != null) ? inputText.Length : 0;
        string raw = ExecuteAiRequest(BuildSystemPrompt(cfg.Style, false, len), inputText, false, cfg, out usedModel, out errorMsg, out elapsedMs);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string cleaned = raw.Trim();
        if (!inputText.TrimStart().StartsWith("\"") && cleaned.StartsWith("\"") && cleaned.EndsWith("\"") && cleaned.Length >= 2)
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2);
        }
        return cleaned;
    }

    public static AiSuggestionResult SuggestWithAi(string inputText, AiConfig cfg, out string errorMsg, out long elapsedMs)
    {
        string usedModel;
        int len = (inputText != null) ? inputText.Length : 0;
        string raw = ExecuteAiRequest(BuildSystemPrompt(cfg.Style, true, len), inputText, true, cfg, out usedModel, out errorMsg, out elapsedMs);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var res = ParseSuggestionResponse(raw, inputText);
        if (res != null) res.ModelUsed = usedModel;
        return res;
    }

    private static AiSuggestionResult ParseSuggestionResponse(string raw, string inputText)
    {
        var result = new AiSuggestionResult();
        string cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            int nl = cleaned.IndexOf('\n');
            if (nl >= 0) cleaned = cleaned.Substring(nl + 1);
            int endFence = cleaned.LastIndexOf("```");
            if (endFence >= 0) cleaned = cleaned.Substring(0, endFence);
            cleaned = cleaned.Trim();
        }

        int firstBrace = cleaned.IndexOf('{');
        int lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            string jsonSlice = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
            try
            {
                var ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 10485760;
                var obj = ser.Deserialize<Dictionary<string, object>>(jsonSlice);
                if (obj != null)
                {
                    if (obj.ContainsKey("hasErrors") && obj["hasErrors"] is bool)
                    {
                        result.HasErrors = (bool)obj["hasErrors"];
                    }
                    object fixesRaw = obj.ContainsKey("fixes") ? obj["fixes"] : (obj.ContainsKey("f") ? obj["f"] : null);
                    var fixesList = fixesRaw as System.Collections.ArrayList;
                    if (fixesList != null)
                    {
                        foreach (object f in fixesList)
                        {
                            string fs = (f == null) ? "" : f.ToString().Trim();
                            if (string.IsNullOrEmpty(fs)) continue;
                            int arrowIdx = fs.IndexOf("->");
                            if (arrowIdx > 0)
                            {
                                string lhs = fs.Substring(0, arrowIdx).Trim().Trim('-', ' ');
                                string rhs = fs.Substring(arrowIdx + 2).Trim();
                                if (lhs.Equals(rhs, StringComparison.Ordinal)) continue;
                            }
                            result.Fixes.Add(fs);
                        }
                    }

                    object optsRaw = obj.ContainsKey("options") ? obj["options"] : (obj.ContainsKey("o") ? obj["o"] : null);
                    var optList = optsRaw as System.Collections.ArrayList;
                    if (optList != null)
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (object o in optList)
                        {
                            var dict = o as Dictionary<string, object>;
                            if (dict == null) continue;
                            object lObj = dict.ContainsKey("label") ? dict["label"] : (dict.ContainsKey("l") ? dict["l"] : null);
                            object tObj = dict.ContainsKey("text") ? dict["text"] : (dict.ContainsKey("t") ? dict["t"] : null);
                            string lbl = (lObj != null) ? lObj.ToString().Trim() : "✨ Propozycja";
                            string txt = (tObj != null) ? tObj.ToString().Trim() : "";
                            if (string.IsNullOrEmpty(txt)) continue;
                            if (!inputText.TrimStart().StartsWith("\"") && txt.StartsWith("\"") && txt.EndsWith("\"") && txt.Length >= 2)
                            {
                                txt = txt.Substring(1, txt.Length - 2);
                            }
                            if (seen.Add(txt))
                            {
                                result.Options.Add(new AiSuggestionOption { Label = lbl, Text = txt });
                            }
                        }
                    }
                    if (result.Options.Count > 0 || !result.HasErrors)
                    {
                        return result;
                    }
                }
            }
            catch { }
        }

        // Graceful plain-text fallback
        if (!inputText.TrimStart().StartsWith("\"") && cleaned.StartsWith("\"") && cleaned.EndsWith("\"") && cleaned.Length >= 2)
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2);
        }
        if (!string.IsNullOrWhiteSpace(cleaned) && !cleaned.Equals(inputText, StringComparison.Ordinal))
        {
            result.HasErrors = true;
            result.Options.Add(new AiSuggestionOption { Label = "✨ Poprawiona wersja", Text = cleaned });
        }
        return result;
    }

    private static string ExecuteAiRequest(string sysPrompt, string inputText, bool jsonMode, AiConfig cfg, out string usedModel, out string errorMsg, out long elapsedMs)
    {
        errorMsg = null;
        elapsedMs = 0;
        usedModel = (cfg != null) ? cfg.Model : "";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            string cleanKey = AiConfig.SanitizeKey(cfg.ApiKey);
            string cacheKey = string.Format("{0}|{1}|{2}|{3}|{4}", cfg.Provider, usedModel, cfg.Style, jsonMode ? "1" : "0", inputText);
            string cached = GetCachedAi(cacheKey);
            if (!string.IsNullOrEmpty(cached))
            {
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                return cached;
            }

            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 10485760;

            string resultText = null;
            int timeoutMs = (inputText != null && inputText.Length > 650) ? 10500 : 6500;

            if (cfg.Provider == "gemini")
            {
                string primaryModel = string.IsNullOrEmpty(cfg.Model) ? "gemini-3.1-flash-lite" : cfg.Model.Trim();
                usedModel = primaryModel;
                int errCode = 0;

                // Szybka asekuracja równoległa: jeśli główny model nie odpowie w ~1.85s, odpalamy równoległy szybki zapas.
                string hedgeBackup = PickFastHedgeModel(primaryModel);
                if (cfg.FastHedgeMode && !string.IsNullOrEmpty(hedgeBackup))
                {
                    int hedgeDelayMs = (inputText != null && inputText.Length > 350) ? 1750 : 1050;
                    resultText = ExecuteGeminiHedged(primaryModel, hedgeBackup, cleanKey, sysPrompt, inputText, jsonMode, timeoutMs, hedgeDelayMs, out usedModel, out errorMsg, out errCode);
                }
                else
                {
                    resultText = CallGeminiSmart(primaryModel, cleanKey, sysPrompt, inputText, jsonMode, timeoutMs, null, out errorMsg, out errCode);
                }

                // Fallback na liste odkrytych modeli przy twardych bledach/limicie
                bool isInvalidKey = (errorMsg != null && (errorMsg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0 || errorMsg.IndexOf("API_KEY", StringComparison.OrdinalIgnoreCase) >= 0));
                if (string.IsNullOrEmpty(resultText) && !isInvalidKey &&
                    (errCode == 0 || errCode == 404 || errCode == 429 || errCode == 503 || errCode == 400))
                {
                    string discErr;
                    var models = DiscoverGeminiModels(cleanKey, false, out discErr);
                    int tries = 0;
                    foreach (string alt in models)
                    {
                        if (alt.Equals(primaryModel, StringComparison.OrdinalIgnoreCase)) continue;
                        if (tries >= 3) break;
                        tries++;

                        string altErr;
                        int altCode;
                        string altRes = CallGeminiSmart(alt, cleanKey, sysPrompt, inputText, jsonMode, timeoutMs, null, out altErr, out altCode);
                        if (!string.IsNullOrEmpty(altRes))
                        {
                            resultText = altRes;
                            errorMsg = null;
                            usedModel = alt;
                            cfg.Model = alt;
                            if (SpacjomatContext.aiConfig != null && cfg.ApiKey == SpacjomatContext.aiConfig.ApiKey)
                            {
                                SpacjomatContext.aiConfig.Model = alt;
                                SpacjomatContext.aiConfig.Save();
                            }
                            SpacjomatContext.Log("AI auto-switched to working Gemini model: " + alt);
                            break;
                        }
                    }
                }
            }
            else
            {
                string url = string.IsNullOrEmpty(cfg.CustomEndpoint)
                    ? "https://api.openai.com/v1/chat/completions"
                    : cfg.CustomEndpoint.Trim();
                string model = string.IsNullOrEmpty(cfg.Model) ? "gpt-4o-mini" : cfg.Model.Trim();
                usedModel = model;

                var payload = new Dictionary<string, object>();
                payload["model"] = model;
                payload["temperature"] = 0.1;
                payload["messages"] = new object[] {
                    new Dictionary<string, object> { { "role", "system" }, { "content", sysPrompt } },
                    new Dictionary<string, object> { { "role", "user" }, { "content", inputText } }
                };

                string body = ser.Serialize(payload);
                var headers = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(cleanKey))
                {
                    headers["Authorization"] = "Bearer " + cleanKey;
                }

                WebHeaderCollection respHeaders;
                HttpWebRequest reqOut;
                string respJson = HttpRequestWithMeta("POST", url, body, headers, timeoutMs, out respHeaders, out reqOut);
                var parsed = ser.Deserialize<Dictionary<string, object>>(respJson);

                int pTok = 0, cTok = 0, tTok = 0;
                if (parsed != null && parsed.ContainsKey("usage"))
                {
                    var u = parsed["usage"] as Dictionary<string, object>;
                    pTok = ReadInt(u, "prompt_tokens");
                    cTok = ReadInt(u, "completion_tokens");
                    tTok = ReadInt(u, "total_tokens");
                }

                if (parsed != null && parsed.ContainsKey("choices"))
                {
                    var choices = parsed["choices"] as System.Collections.ArrayList;
                    if (choices != null && choices.Count > 0)
                    {
                        var ch0 = choices[0] as Dictionary<string, object>;
                        if (ch0 != null && ch0.ContainsKey("message"))
                        {
                            var msg = ch0["message"] as Dictionary<string, object>;
                            if (msg != null && msg.ContainsKey("content"))
                            {
                                resultText = (string)msg["content"];
                            }
                        }
                    }
                }

                AiUsageTracker.RecordResponse("openai", model, !string.IsNullOrWhiteSpace(resultText), 200, pTok, cTok, tTok, respHeaders, null);
            }

            if (string.IsNullOrWhiteSpace(resultText))
            {
                if (string.IsNullOrEmpty(errorMsg)) errorMsg = "Pusta odpowiedź z serwera AI.";
                sw.Stop();
                elapsedMs = sw.ElapsedMilliseconds;
                return null;
            }

            string trimmed = resultText.Trim();
            PutCachedAi(cacheKey, trimmed);
            sw.Stop();
            elapsedMs = sw.ElapsedMilliseconds;
            return trimmed;
        }
        catch (WebException wex)
        {
            int code;
            errorMsg = ParseHttpError(wex, out code);
            WebHeaderCollection errHdrs = (wex.Response != null) ? wex.Response.Headers : null;
            AiUsageTracker.RecordResponse(cfg.Provider, usedModel, false, code, 0, 0, 0, errHdrs, errorMsg);
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            AiUsageTracker.RecordResponse(cfg.Provider, usedModel, false, 0, 0, 0, 0, null, errorMsg);
        }

        sw.Stop();
        elapsedMs = sw.ElapsedMilliseconds;
        return null;
    }

    private static string PickFastHedgeModel(string primaryModel)
    {
        string p = (primaryModel ?? "").ToLowerInvariant();
        string[] pool = new string[] { "gemini-3.1-flash-lite", "gemini-3.5-flash-lite", "gemini-flash-lite-latest" };
        foreach (string cand in pool)
        {
            if (!cand.Equals(p, StringComparison.OrdinalIgnoreCase)) return cand;
        }
        return null;
    }

    private static string ExecuteGeminiHedged(
        string primaryModel, string backupModel, string cleanKey, string sysPrompt, string inputText,
        bool jsonMode, int timeoutMs, int hedgeDelayMs, out string usedModel, out string errorMsg, out int errCode)
    {
        usedModel = primaryModel;
        errorMsg = null;
        errCode = 0;

        object winLock = new object();
        int winnerState = 0; // 0=pending, 1=done success, 2=both done failed
        int finishedCount = 0;
        bool needBoth = false;
        string winText = null, winModel = primaryModel, lastErr = null;
        int lastCode = 0;
        HttpWebRequest reqPrimary = null;
        HttpWebRequest reqBackup = null;
        var doneEvent = new ManualResetEvent(false);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            string e; int c;
            string r = CallGeminiSmart(primaryModel, cleanKey, sysPrompt, inputText, jsonMode, timeoutMs, req => { lock (winLock) reqPrimary = req; }, out e, out c);
            lock (winLock)
            {
                finishedCount++;
                if (winnerState == 0 && !string.IsNullOrEmpty(r))
                {
                    winnerState = 1;
                    winText = r;
                    winModel = primaryModel;
                    try { if (reqBackup != null) reqBackup.Abort(); } catch { }
                    doneEvent.Set();
                }
                else
                {
                    if (!string.IsNullOrEmpty(e)) { lastErr = e; lastCode = c; }
                    if (!needBoth || finishedCount >= 2)
                    {
                        if (winnerState == 0) winnerState = 2;
                        doneEvent.Set();
                    }
                }
            }
        });

        if (!doneEvent.WaitOne(hedgeDelayMs))
        {
            bool launchBackup = false;
            lock (winLock)
            {
                if (winnerState == 0)
                {
                    needBoth = true;
                    launchBackup = true;
                }
            }

            if (launchBackup)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    string e; int c;
                    string r = CallGeminiSmart(backupModel, cleanKey, sysPrompt, inputText, jsonMode, timeoutMs, req => { lock (winLock) reqBackup = req; }, out e, out c);
                    lock (winLock)
                    {
                        finishedCount++;
                        if (winnerState == 0 && !string.IsNullOrEmpty(r))
                        {
                            winnerState = 1;
                            winText = r;
                            winModel = backupModel;
                            try { if (reqPrimary != null) reqPrimary.Abort(); } catch { }
                            doneEvent.Set();
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(e) && string.IsNullOrEmpty(lastErr)) { lastErr = e; lastCode = c; }
                            if (finishedCount >= 2)
                            {
                                if (winnerState == 0) winnerState = 2;
                                doneEvent.Set();
                            }
                        }
                    }
                });
            }
            doneEvent.WaitOne(timeoutMs + 800);
        }

        doneEvent.Close();
        usedModel = winModel;
        errorMsg = lastErr;
        errCode = lastCode;
        return winText;
    }

    private static string CallGeminiSmart(string model, string cleanKey, string sysPrompt, string inputText, bool jsonMode, int timeoutMs, Action<HttpWebRequest> onReqCreated, out string errorMsg, out int httpCode)
    {
        string thinkMode = ResolveThinkingMode(model);
        string res = CallGeminiOnce(model, cleanKey, sysPrompt, inputText, jsonMode, thinkMode, timeoutMs, onReqCreated, out errorMsg, out httpCode);
        if (!string.IsNullOrEmpty(res)) return res;

        // Jesli thinkingConfig wywolal HTTP 400 na nowym/nieznanym modelu, przejdz automatycznie i zapamietaj
        if (httpCode == 400 && errorMsg != null && errorMsg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) < 0 && thinkMode != "none")
        {
            string nextMode = (thinkMode == "budget0") ? "minimal" : "none";
            RememberThinkingMode(model, nextMode);
            res = CallGeminiOnce(model, cleanKey, sysPrompt, inputText, jsonMode, nextMode, timeoutMs, onReqCreated, out errorMsg, out httpCode);
            if (!string.IsNullOrEmpty(res)) return res;

            if (httpCode == 400 && nextMode != "none")
            {
                RememberThinkingMode(model, "none");
                res = CallGeminiOnce(model, cleanKey, sysPrompt, inputText, jsonMode, "none", timeoutMs, onReqCreated, out errorMsg, out httpCode);
                if (!string.IsNullOrEmpty(res)) return res;
            }
        }

        if (string.IsNullOrEmpty(res) && jsonMode && httpCode == 400 && errorMsg != null && errorMsg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) < 0)
        {
            res = CallGeminiOnce(model, cleanKey, sysPrompt, inputText, false, ResolveThinkingMode(model), timeoutMs, onReqCreated, out errorMsg, out httpCode);
        }
        return res;
    }

    private static string CallGeminiOnce(string model, string cleanKey, string sysPrompt, string inputText, bool jsonMode, string thinkMode, int timeoutMs, Action<HttpWebRequest> onReqCreated, out string errorMsg, out int httpCode)
    {
        errorMsg = null;
        httpCode = 0;
        try
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 10485760;

            string url = string.Format("https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent", model);
            var headers = new Dictionary<string, string>();
            headers["x-goog-api-key"] = cleanKey;

            var payload = new Dictionary<string, object>();
            var sysInst = new Dictionary<string, object>();
            sysInst["parts"] = new object[] { new Dictionary<string, object> { { "text", sysPrompt } } };
            payload["systemInstruction"] = sysInst;

            var content = new Dictionary<string, object>();
            content["role"] = "user";
            content["parts"] = new object[] { new Dictionary<string, object> { { "text", inputText } } };
            payload["contents"] = new object[] { content };

            var genCfg = new Dictionary<string, object>();
            genCfg["temperature"] = 0.1;
            int inLen = (inputText != null) ? inputText.Length : 100;
            genCfg["maxOutputTokens"] = jsonMode ? Math.Max(220, Math.Min(2048, inLen * 2 + 220)) : Math.Max(128, Math.Min(2048, inLen * 2 + 120));
            if (jsonMode) genCfg["responseMimeType"] = "application/json";
            if (thinkMode == "minimal")
            {
                genCfg["thinkingConfig"] = new Dictionary<string, object> { { "thinkingLevel", "minimal" } };
            }
            else if (thinkMode == "budget0")
            {
                genCfg["thinkingConfig"] = new Dictionary<string, object> { { "thinkingBudget", 0 } };
            }
            payload["generationConfig"] = genCfg;

            string body = ser.Serialize(payload);
            WebHeaderCollection respHeaders;
            HttpWebRequest reqRef;
            string respJson = HttpRequestWithMeta("POST", url, body, headers, timeoutMs, out respHeaders, out reqRef, onReqCreated);
            var parsed = ser.Deserialize<Dictionary<string, object>>(respJson);

            int pTok = 0, cTok = 0, tTok = 0;
            if (parsed != null && parsed.ContainsKey("usageMetadata"))
            {
                var u = parsed["usageMetadata"] as Dictionary<string, object>;
                pTok = ReadInt(u, "promptTokenCount");
                cTok = ReadInt(u, "candidatesTokenCount");
                tTok = ReadInt(u, "totalTokenCount");
            }

            if (parsed != null && parsed.ContainsKey("candidates"))
            {
                var cands = parsed["candidates"] as object[];
                if (cands == null && parsed["candidates"] is System.Collections.ArrayList)
                {
                    cands = ((System.Collections.ArrayList)parsed["candidates"]).ToArray();
                }
                if (cands != null && cands.Length > 0)
                {
                    var c0 = cands[0] as Dictionary<string, object>;
                    if (c0 != null && c0.ContainsKey("content"))
                    {
                        var cont = c0["content"] as Dictionary<string, object>;
                        if (cont != null && cont.ContainsKey("parts"))
                        {
                            var partsList = cont["parts"] as System.Collections.ArrayList;
                            if (partsList != null && partsList.Count > 0)
                            {
                                var p0 = partsList[0] as Dictionary<string, object>;
                                if (p0 != null && p0.ContainsKey("text"))
                                {
                                    AiUsageTracker.RecordResponse("gemini", model, true, 200, pTok, cTok, tTok, respHeaders, null);
                                    return (string)p0["text"];
                                }
                            }
                        }
                    }
                }
            }
            errorMsg = "Nieoczekiwana struktura odpowiedzi Gemini.";
            AiUsageTracker.RecordResponse("gemini", model, false, 200, pTok, cTok, tTok, respHeaders, errorMsg);
        }
        catch (WebException wex)
        {
            if (wex.Status == WebExceptionStatus.RequestCanceled)
            {
                errorMsg = "Anulowano wolniejsze zapytanie.";
                return null;
            }
            errorMsg = ParseHttpError(wex, out httpCode);
            WebHeaderCollection errHdrs = (wex.Response != null) ? wex.Response.Headers : null;
            AiUsageTracker.RecordResponse("gemini", model, false, httpCode, 0, 0, 0, errHdrs, errorMsg);
        }
        catch (Exception ex)
        {
            errorMsg = ex.Message;
            AiUsageTracker.RecordResponse("gemini", model, false, 0, 0, 0, 0, null, errorMsg);
        }
        return null;
    }

    private static string HttpRequestWithMeta(string method, string url, string jsonBody, Dictionary<string, string> extraHeaders, int timeoutMs, out WebHeaderCollection respHeaders, out HttpWebRequest reqOut, Action<HttpWebRequest> onReqCreated = null)
    {
        respHeaders = null;
        HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
        reqOut = req;
        if (onReqCreated != null) onReqCreated(req);
        req.Method = method;
        req.Proxy = null;
        req.KeepAlive = true;
        req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        req.Timeout = timeoutMs;
        req.ReadWriteTimeout = timeoutMs;
        if (extraHeaders != null)
        {
            foreach (var kv in extraHeaders) req.Headers[kv.Key] = kv.Value;
        }
        if (method == "POST")
        {
            req.ContentType = "application/json; charset=utf-8";
            byte[] data = Encoding.UTF8.GetBytes(jsonBody ?? "");
            req.ContentLength = data.Length;
            using (Stream st = req.GetRequestStream())
            {
                st.Write(data, 0, data.Length);
            }
        }
        using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
        {
            respHeaders = resp.Headers;
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }
    }
}

public class SpacjomatContext : ApplicationContext
{
    // Win32 Constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const uint LLKHF_INJECTED = 0x00000010;

    private const int VK_BACK = 0x08;
    private const int VK_TAB = 0x09;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_DELETE = 0x2E;
    private const int VK_F7 = 0x76;
    private const int VK_F8 = 0x77;
    private const int VK_F9 = 0x78;
    private const int VK_K = 0x4B;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr ProcessId);

    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint GetWindowPid(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(int dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex, StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    public static void ShowTopmostNoActivate(Form f)
    {
        if (f == null || f.IsDisposed) return;
        try
        {
            if (!f.Visible) ShowWindow(f.Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(f.Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    // Knowledge Base & State
    public static Dictionary<string, string> directRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, string> bigramRules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public static HashSet<string> whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, List<string>> strippedDict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<int, Dictionary<char, List<KeyValuePair<string, int>>>> wordsByLenAndChar = new Dictionary<int, Dictionary<char, List<KeyValuePair<string, int>>>>();
    public static Dictionary<int, List<KeyValuePair<string, int>>> topWordsByLength = new Dictionary<int, List<KeyValuePair<string, int>>>();
    public static HashSet<string> sessionIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static ulong[] sjpHashes = new ulong[0];
    private static bool sjpIs48Bit = false;
    public static int learnedRulesCount = 0;

    public static HashSet<string> ambiguousKeepAscii = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "ze", "sa", "nadal", "sama", "sprawie", "pojawia", "jakich", "czesc", "wlasna", "mala", "duza", "nowa", "stara",
        "dobra", "zla", "caly", "tez", "ok", "ma", "do", "od", "na", "po", "za", "pod", "nad", "bez", "pan",
        "ten", "tak", "dom", "las", "rok", "kot", "test", "kod", "plik", "krok", "olej", "miliona", "milionom",
        "niezla", "pol", "dam", "mam", "mamie", "tato", "tate", "dzialo", "rano", "noca", "okej"
    };

    public static HashSet<string> commaConjunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "że", "żeby", "aby", "iż", "bo", "ponieważ", "gdyż", "bowiem",
        "który", "która", "które", "których", "którym", "którymi", "którego", "której",
        "ale", "lecz", "jednak", "chociaż", "choć", "jeśli", "jeżeli", "więc", "zatem"
    };

    public static HashSet<string> abbreviations = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "np.", "itd.", "itp.", "tzn.", "tzw.", "m.in.", "prof.", "dr.", "ul.", "godz.", "art."
    };

    public static AiConfig aiConfig = new AiConfig();

    private static StringBuilder currentWord = new StringBuilder();
    private static string previousLiveWord = "";
    private static string lastOriginalWord = "";
    private static string lastCorrectedWord = "";
    private static bool lastWasAutocorrected = false;
    public static bool isEnabled = true;
    public static bool isAggressiveMode = false;
    private static volatile bool isSimulatingInput = false;
    private static IntPtr lastForegroundHwnd = IntPtr.Zero;
    private static IntPtr lastFocusHwnd = IntPtr.Zero;
    private static bool suppressNextKUp = false;
    private static bool suppressNextLUp = false;
    private static bool suppressNextF7Up = false;
    private static bool suppressNextF8Up = false;
    private static bool suppressNextF9Up = false;
    private static readonly byte[] hookKeyState = new byte[256];
    private static readonly StringBuilder hookCharBuffer = new StringBuilder(10);
    private static EventWaitHandle pingEvent = null;
    private static volatile bool stopPingThread = false;

    private static IntPtr hookId = IntPtr.Zero;
    private static LowLevelKeyboardProc hookProc;

    public static SpacjomatContext Instance = null;
    private NotifyIcon trayIcon;
    private ContextMenuStrip trayMenu;
    private ToolStripMenuItem statusMenuItem;
    private ToolStripMenuItem modeMenuItem;
    private ToolStripMenuItem pillMenuItem;
    private ToolStripMenuItem suggestModeMenuItem;
    private ToolStripMenuItem autostartMenuItem;
    private Icon appIcon;
    private SpacjomatStudioForm studioWindow = null;
    private AiSettingsForm settingsWindow = null;
    public static FloatingBanner floatingBanner = null;
    public static GrammarlyPillForm grammarlyPill = null;
    public static GrammarlySuggestionForm suggestionPopup = null;
    private HotkeyWindow hotkeyWindow = null;
    private System.Windows.Forms.Timer hotReloadTimer = null;
    private DateTime startupCsWriteUtc = DateTime.MinValue;
    private DateTime pendingReloadUtc = DateTime.MinValue;

    // =====================================================================
    // STABILNOSC: sledzenie okna docelowego, watchdog haka, diagnostyka
    // =====================================================================
    private static readonly uint ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    private static IntPtr lastExternalHwnd = IntPtr.Zero;
    private static int correctionStartTick = 0;
    private static int correctionGen = 0;
    private static int hookReinstallCount = 0;
    private static int slowHookCount = 0;
    private static int lastHookTick = 0;
    private static int lastHookVk = 0;
    private static int lastAiTriggerTick = 0;
    private static int lastOfflineTriggerTick = 0;
    private static volatile bool isSessionLocked = false;
    private static volatile bool isSuspended = false;
    public static volatile bool IntentionalExit = false;
    private System.Windows.Forms.Timer watchdogTimer = null;
    private System.Windows.Forms.Timer targetTrackTimer = null;
    private System.Windows.Forms.Timer delayedRehookTimer = null;
    private int watchdogTicks = 0;

    // Czy okno nalezy do Spacjomatu albo do paska zadan / menu (nie nadaje sie na cel korekty).
    private static bool IsOwnOrShellWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        try
        {
            uint pid;
            GetWindowPid(hwnd, out pid);
            if (pid == ownPid) return true;
            StringBuilder sb = new StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0)
            {
                string cn = sb.ToString();
                if (cn == "Shell_TrayWnd" || cn == "Shell_SecondaryTrayWnd" || cn == "NotifyIconOverflowWindow" ||
                    cn == "TopLevelWindowForOverflowXamlIsland" || cn == "#32768") return true;
            }
        }
        catch { }
        return false;
    }

    private static void TrackExternalWindow()
    {
        try
        {
            IntPtr fg = GetForegroundWindow();
            if (!IsOwnOrShellWindow(fg)) lastExternalHwnd = fg;
        }
        catch { }
    }

    // Okno, w ktorym uzytkownik pisze (nawet gdy klika menu przy zegarku albo pigulke).
    private static IntPtr ResolveTargetWindow()
    {
        IntPtr fg = GetForegroundWindow();
        if (!IsOwnOrShellWindow(fg)) { lastExternalHwnd = fg; return fg; }
        IntPtr last = lastExternalHwnd;
        if (last != IntPtr.Zero && IsWindow(last)) return last;
        return fg;
    }

    private static void EnsureTargetForeground(IntPtr target)
    {
        if (target == IntPtr.Zero) return;
        bool switched = false;
        for (int i = 0; i < 6; i++)
        {
            if (GetForegroundWindow() == target) break;
            SetForegroundWindow(target);
            switched = true;
            Thread.Sleep(22);
        }
        if (switched) Thread.Sleep(35);
    }

    private static bool InstallHook()
    {
        try
        {
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
            if (hookProc == null) hookProc = HookCallback;
            hookId = SetWindowsHookEx(WH_KEYBOARD_LL, hookProc, GetModuleHandle(null), 0);
            if (hookId == IntPtr.Zero)
            {
                Log("Stability: SetWindowsHookEx failed, Win32 error " + Marshal.GetLastWin32Error());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log("Stability: InstallHook error: " + ex.Message);
            return false;
        }
    }

    // Windows po cichu wylacza hak klawiatury po uspieniu, blokadzie sesji albo zbyt wolnym callbacku.
    private static void ReinstallHookSafe(string reason)
    {
        if (isSimulatingInput || isSessionLocked || isSuspended) return;
        // Nie zdejmuj dzialajacego haka w polowie wpisywanego slowa podczas rutynowego watchdoga.
        if (reason == "watchdog" && currentWord.Length > 0 && lastHookTick != 0 && unchecked(Environment.TickCount - lastHookTick) < 2500)
        {
            return;
        }
        if (InstallHook())
        {
            hookReinstallCount++;
            if (reason != "watchdog") Log("Stability: hak klawiatury zalozony ponownie (" + reason + ")");
        }
    }

    private static void RunOnUi(Action a)
    {
        try
        {
            Form f = floatingBanner;
            if (f != null && !f.IsDisposed && f.IsHandleCreated && f.InvokeRequired) f.BeginInvoke(a);
            else a();
        }
        catch (Exception ex) { Log("Stability: RunOnUi error: " + ex.Message); }
    }

    private static void EnsureOnScreen(Form f)
    {
        try
        {
            if (f == null || f.IsDisposed) return;
            Rectangle b = f.Bounds;
            bool visible = false;
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle r = Rectangle.Intersect(sc.WorkingArea, b);
                if (r.Width >= Math.Min(60, b.Width) && r.Height >= Math.Min(20, b.Height)) { visible = true; break; }
            }
            if (!visible)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                f.Location = new Point(wa.Right - f.Width - 20, wa.Bottom - f.Height - 20);
                Log("Stability: okno poza ekranem - przywrocono pozycje.");
            }
        }
        catch { }
    }

    private void EnsureHotkeysRegistered(bool forceReRegister = false)
    {
        try
        {
            if (hotkeyWindow == null) { hotkeyWindow = new HotkeyWindow(); return; }
            hotkeyWindow.EnsureRegistered(forceReRegister);
        }
        catch (Exception ex) { Log("Stability: ponowna rejestracja skrotow nie powiodla sie: " + ex.Message); }
    }

    private void ScheduleRehook(string reason)
    {
        RunOnUi(() =>
        {
            Log("Stability: zaplanowano odswiezenie hakow (" + reason + ")");
            if (delayedRehookTimer == null)
            {
                delayedRehookTimer = new System.Windows.Forms.Timer();
                delayedRehookTimer.Interval = 1400;
                delayedRehookTimer.Tick += (s2, e2) =>
                {
                    delayedRehookTimer.Stop();
                    if (!isSessionLocked && !isSuspended)
                    {
                        ReinstallHookSafe("po zdarzeniu systemowym");
                        EnsureHotkeysRegistered(true);
                        EnsureOnScreen(grammarlyPill);
                        EnsureOnScreen(suggestionPopup);
                        AiCorrectionEngine.WarmUpConnectionAsync(aiConfig);
                    }
                };
            }
            delayedRehookTimer.Stop();
            delayedRehookTimer.Start();
        });
    }

    private void StartStabilityWatchdogs()
    {
        try
        {
            targetTrackTimer = new System.Windows.Forms.Timer();
            targetTrackTimer.Interval = 150;
            targetTrackTimer.Tick += (s, e) => TrackExternalWindow();
            targetTrackTimer.Start();

            watchdogTimer = new System.Windows.Forms.Timer();
            watchdogTimer.Interval = 15000;
            watchdogTimer.Tick += (s, e) =>
            {
                try
                {
                    watchdogTicks++;
                    if (!isSessionLocked && !isSuspended)
                    {
                        if (hookId == IntPtr.Zero) ReinstallHookSafe("brak haka");
                        else if (watchdogTicks % 3 == 0) ReinstallHookSafe("watchdog");
                        EnsureHotkeysRegistered(false);
                        EnsurePillAlwaysOnDesktop();
                    }

                    // Utrzymuj cieply TLS do AI, jesli uzytkownik pisal w ciagu ostatnich 10 minut
                    if (lastHookTick != 0 && unchecked(Environment.TickCount - lastHookTick) < 600000)
                    {
                        AiCorrectionEngine.MaybeKeepAliveAsync(aiConfig);
                    }

                    if (Interlocked.CompareExchange(ref correctionRunning, 0, 0) != 0 &&
                        unchecked(Environment.TickCount - correctionStartTick) > 22000)
                    {
                        Log("Stability: watchdog zwolnil zawieszona korekte.");
                        Interlocked.Exchange(ref correctionRunning, 0);
                        isSimulatingInput = false;
                    }
                }
                catch (Exception ex) { Log("Stability: blad watchdoga: " + ex.Message); }
            };
            watchdogTimer.Start();

            Microsoft.Win32.SystemEvents.PowerModeChanged += (s, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
                {
                    isSuspended = true;
                }
                else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                {
                    isSuspended = false;
                    isSessionLocked = false;
                    ScheduleRehook("wznowienie z uspienia");
                }
            };
            Microsoft.Win32.SystemEvents.SessionSwitch += (s, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock)
                {
                    isSessionLocked = true;
                }
                else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon)
                {
                    isSessionLocked = false;
                    isSuspended = false;
                    ScheduleRehook("odblokowanie sesji");
                }
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => ScheduleRehook("zmiana ekranow / dokowania");
        }
        catch (Exception ex) { Log("Stability: StartStabilityWatchdogs error: " + ex.Message); }
    }

    private static string GetAutostartShortcutPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Spacjomat.lnk");
    }

    public static bool IsAutostartEnabled()
    {
        try { return File.Exists(GetAutostartShortcutPath()); } catch { return false; }
    }

    public static bool SetAutostart(bool enable)
    {
        try
        {
            string lnkPath = GetAutostartShortcutPath();
            if (!enable)
            {
                if (File.Exists(lnkPath)) File.Delete(lnkPath);
                return true;
            }
            string appDir = GetAppDir().TrimEnd('\\');
            string vbsPath = Path.Combine(appDir, "Spacjomat.vbs");
            string icoPath = Path.Combine(appDir, "Spacjomat.ico");
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return false;
            object shell = Activator.CreateInstance(shellType);
            object lnk = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type lnkType = lnk.GetType();
            lnkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "wscript.exe" });
            lnkType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { "\"" + vbsPath + "\"" });
            lnkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { appDir });
            if (File.Exists(icoPath))
            {
                lnkType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, lnk, new object[] { icoPath });
            }
            lnkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, lnk, null);
            return File.Exists(lnkPath);
        }
        catch (Exception ex)
        {
            Log("Autostart error: " + ex.Message);
            return false;
        }
    }

    private void EnsurePillAlwaysOnDesktop()
    {
        try
        {
            if (aiConfig != null && aiConfig.ShowPill && grammarlyPill != null && !grammarlyPill.IsDisposed)
            {
                EnsureOnScreen(grammarlyPill);
                ShowTopmostNoActivate(grammarlyPill);
            }
        }
        catch { }
    }

    private static bool IsRunningAsAdmin()
    {
        try
        {
            System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static bool IsAppDirWritable()
    {
        try
        {
            string p = Path.Combine(GetAppDir(), ".write_test.tmp");
            File.WriteAllText(p, "ok");
            File.Delete(p);
            return true;
        }
        catch { return false; }
    }

    public void ShowDiagnostics()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("DIAGNOSTYKA SPACJOMATU");
        sb.AppendLine();
        sb.AppendLine("Hak klawiatury (WH_KEYBOARD_LL): " + (hookId != IntPtr.Zero ? "AKTYWNY" : "NIEAKTYWNY!"));
        sb.AppendLine("   odswiezony: " + hookReinstallCount + " razy, wolne wywolania: " + slowHookCount);
        sb.AppendLine("Skroty systemowe: " + (hotkeyWindow != null ? hotkeyWindow.Summary() : "brak okna skrotow!"));
        string lastKey = (lastHookTick == 0)
            ? "jeszcze zaden"
            : string.Format("0x{0:X2} ({1}), {2:0.0} s temu", lastHookVk, ((Keys)lastHookVk).ToString(), unchecked(Environment.TickCount - lastHookTick) / 1000.0);
        sb.AppendLine("Ostatni klawisz widziany przez Spacjomat: " + lastKey);
        string aiModelName = (aiConfig != null && !string.IsNullOrEmpty(aiConfig.Model)) ? aiConfig.Model : "-";
        sb.AppendLine("AI (F7 / Ctrl+Shift+L): " + (AiCorrectionEngine.IsAiReady(aiConfig) ? ("gotowe (" + aiModelName + ", TurboHedge=" + (aiConfig.FastHedgeMode ? "WL" : "WYL") + ")") : "wylaczone (wklej klucz w ustawieniach)"));
        sb.AppendLine(string.Format("Baza Offline: {0:N0} regul (w tym {1} wyuczonych z AI) | {2:N0} slow | {3:N0} bigramow | {4} wyjatkow",
            directRules.Count, learnedRulesCount, dictionary.Count, bigramRules.Count, whitelist.Count));
        sb.AppendLine("Uprawnienia: " + (IsRunningAsAdmin() ? "Administrator (pelny dostep takze do okien Admin)" : "zwykly uzytkownik"));
        sb.AppendLine("Folder: " + GetAppDir() + (IsAppDirWritable() ? "" : "   (BRAK ZAPISU!)"));
        sb.AppendLine();
        sb.AppendLine("JAK PRZETESTOWAC: nacisnij F7 w dowolnym oknie, potem otworz diagnostyke ponownie.");
        sb.AppendLine("Jesli 'ostatni klawisz' nie jest F7 / F8, to klawisz nie dociera do Windows");
        sb.AppendLine("(laptop z wlaczonym Fn Lock albo klawiatura wysyla funkcje multimedialne).");
        sb.AppendLine("Wtedy uzyj Fn+F7 / Fn+F8 albo skrotow, ktore dzialaja zawsze:");
        sb.AppendLine("   Ctrl+Shift+L = AI (zamiast F7)");
        sb.AppendLine("   Ctrl+Shift+K = baza Offline (zamiast F8)");
        MessageBox.Show(sb.ToString(), "Spacjomat - diagnostyka", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static string GetCurrentDesktopName()
    {
        try
        {
            IntPtr hDesk = GetThreadDesktop(GetCurrentThreadId());
            if (hDesk != IntPtr.Zero)
            {
                StringBuilder sb = new StringBuilder(256);
                int needed;
                if (GetUserObjectInformationW(hDesk, 2, sb, sb.Capacity * 2, out needed))
                {
                    return sb.ToString();
                }
            }
        }
        catch { }
        return "Default";
    }

    public static string GetAppDir()
    {
        try
        {
            string envDir = Environment.GetEnvironmentVariable("SPACJOMAT_DIR");
            if (!string.IsNullOrEmpty(envDir) && Directory.Exists(envDir))
            {
                return envDir;
            }
            string cwd = Directory.GetCurrentDirectory();
            if (!string.IsNullOrEmpty(cwd) && File.Exists(Path.Combine(cwd, "Spacjomat.cs")))
            {
                return cwd;
            }
        }
        catch { }
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string dir = Path.Combine(desktop, "Spacjomat");
        if (Directory.Exists(dir)) return dir;
        return AppDomain.CurrentDomain.BaseDirectory;
    }

    public static ulong Fnv1a64(string s)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < s.Length; i++)
        {
            h ^= (ushort)s[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    public static bool IsValidPolishWord(string lower)
    {
        if (string.IsNullOrEmpty(lower)) return false;
        if (dictionary.ContainsKey(lower)) return true;
        if (sjpHashes.Length > 0)
        {
            ulong h = Fnv1a64(lower);
            if (sjpIs48Bit) h &= 0x0000FFFFFFFFFFFFUL;
            return Array.BinarySearch(sjpHashes, h) >= 0;
        }
        return false;
    }

    public SpacjomatContext()
    {
        Instance = this;
        try
        {
            InitializeApplication();
        }
        catch (Exception ex)
        {
            Log("Init error: " + ex.ToString());
            MessageBox.Show("Błąd uruchamiania Spacjomatu: " + ex.Message, "Spacjomat", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void InitializeApplication()
    {
        aiConfig = AiConfig.Load();
        AiCorrectionEngine.WarmUpConnectionAsync(aiConfig);
        LoadAllDictionaries();

        string baseDir = GetAppDir();
        string iconPath = Path.Combine(baseDir, "Spacjomat.ico");
        if (File.Exists(iconPath))
        {
            try { appIcon = new Icon(iconPath); } catch { appIcon = SystemIcons.Application; }
        }
        else
        {
            appIcon = SystemIcons.Application;
        }

        floatingBanner = new FloatingBanner();
        suggestionPopup = new GrammarlySuggestionForm();
        grammarlyPill = new GrammarlyPillForm(this);
        grammarlyPill.ApplyConfig(aiConfig);
        if (aiConfig.ShowPill)
        {
            grammarlyPill.Show();
        }
        grammarlyPill.RefreshStatus();

        // Native Windows Kernel Hotkey Listener
        try
        {
            hotkeyWindow = new HotkeyWindow();
        }
        catch (Exception ex)
        {
            Log("HotkeyWindow init error: " + ex.Message);
        }

        trayMenu = new ContextMenuStrip();

        var openStudioItem = new ToolStripMenuItem("Otwórz Edytor Długich Tekstów (Studio)", null, (s, e) => OpenStudio());
        openStudioItem.Font = new Font(openStudioItem.Font, FontStyle.Bold);
        trayMenu.Items.Add(openStudioItem);

        trayMenu.Items.Add("🧠 Popraw cały tekst z AI (F7 / Ctrl+Shift+L)", null, (s, e) => TriggerInPlaceCorrection(true, true, false));
        trayMenu.Items.Add("⚡ Szybka korekta Bazą Offline (F8 / Ctrl+Shift+K)", null, (s, e) => TriggerInPlaceCorrection(false, false, true));
        trayMenu.Items.Add("Ustawienia AI (Gemini / Groq / OpenAI) i Limitów...", null, (s, e) => OpenAiSettings());

        trayMenu.Items.Add(new ToolStripSeparator());

        statusMenuItem = new ToolStripMenuItem("Autokorekta w locie: WŁĄCZONA (F9 = Pauza)", null, (s, e) => ToggleState());
        trayMenu.Items.Add(statusMenuItem);

        modeMenuItem = new ToolStripMenuItem("Tryb Offline: Bezpieczny (Zalecany)", null, (s, e) => ToggleMode());
        trayMenu.Items.Add(modeMenuItem);

        pillMenuItem = new ToolStripMenuItem("Pokaż pigułkę Grammarly na ekranie", null, (s, e) => TogglePill());
        pillMenuItem.Checked = aiConfig.ShowPill;
        trayMenu.Items.Add(pillMenuItem);

        autostartMenuItem = new ToolStripMenuItem("🚀 Uruchamiaj z Windows (Autostart)", null, (s, e) => {
            bool target = !IsAutostartEnabled();
            if (SetAutostart(target))
            {
                autostartMenuItem.Checked = target;
                if (floatingBanner != null) floatingBanner.ShowMessage(target ? "🚀 Autostart WŁĄCZONY — Spacjomat będzie zawsze startował z Windows!" : "Autostart wyłączony.");
            }
        });
        autostartMenuItem.Checked = IsAutostartEnabled();
        trayMenu.Items.Add(autostartMenuItem);

        suggestModeMenuItem = new ToolStripMenuItem("Tryb Grammarly: Pokazuj podpowiedzi do wyboru", null, (s, e) => ToggleSuggestMode());
        suggestModeMenuItem.Checked = (aiConfig.CorrectionMode != "auto");
        trayMenu.Items.Add(suggestModeMenuItem);

        trayMenu.Items.Add("Cofnij ostatnie słowo (Backspace)", null, (s, e) => UndoLastCorrection());
        trayMenu.Items.Add("Dodaj ostatnie słowo do słownika", null, (s, e) => AddLastWordToWhitelist());
        trayMenu.Items.Add(new ToolStripSeparator());

        trayMenu.Items.Add("Otwórz folder ze słownikami", null, (s, e) => {
            try { System.Diagnostics.Process.Start(GetAppDir()); } catch { }
        });

        trayMenu.Items.Add("Przeładuj słowniki", null, (s, e) => {
            LoadAllDictionaries();
            ShowBalloon("Spacjomat", "Baza 4.5M słów, reguł i bigramów przeładowana!");
        });

        trayMenu.Items.Add("🔄 Uruchom ponownie (Restart)", null, (s, e) => RestartApplication());
        trayMenu.Items.Add("🩺 Diagnostyka (gdy F7/F8 nie działa)", null, (s, e) => ShowDiagnostics());
        trayMenu.Items.Add("📚 Otwórz wyuczone słowa (AI -> Offline)", null, (s, e) => {
            try {
                string lp = Path.Combine(GetAppDir(), "nauczone_reguly.txt");
                if (!File.Exists(lp)) File.WriteAllText(lp, "# Słowa wyuczone automatycznie z korekt AI (F7)\r\n", new UTF8Encoding(false));
                System.Diagnostics.Process.Start("notepad.exe", lp);
            } catch { }
        });

        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Zakończ Spacjomat", null, (s, e) => {
            IntentionalExit = true;
            try { Environment.ExitCode = 99; } catch { }
            ExitThread();
        });

        trayIcon = new NotifyIcon();
        trayIcon.Text = "Spacjomat - Hybrydowa Autokorekta AI + Offline";
        trayIcon.Icon = appIcon;
        trayIcon.ContextMenuStrip = trayMenu;
        trayIcon.Visible = true;

        trayIcon.DoubleClick += (s, e) => OpenStudio();

        hookProc = HookCallback;
        InstallHook();

        if (hookId == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Log("Failed to set hook. Win32 error: " + err);
        }
        else
        {
            string desk = GetCurrentDesktopName();
            Log(string.Format("Spacjomat v6 started with hookId={0} on Desktop={1}", hookId, desk));
            string aiTag = AiCorrectionEngine.IsAiReady(aiConfig) ? "AI Aktywne" : "Tryb Offline (4.5M SJP + GBoard)";
            bool silentStart = (Environment.GetEnvironmentVariable("SPACJOMAT_SILENT") == "1");
            if (!silentStart)
            {
                try
                {
                    if (floatingBanner != null)
                    {
                        floatingBanner.ShowMessage("🚀 Spacjomat działa! (" + aiTag + " | F7 = AI | F8 = Offline)");
                    }
                }
                catch { }
            }

            try
            {
                string safeDesk = Regex.Replace(desk, @"[^A-Za-z0-9_]", "_");
                pingEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Spacjomat_Ping_v12_" + safeDesk);
                Thread pingThread = new Thread(() =>
                {
                    while (!stopPingThread)
                    {
                        try
                        {
                            if (pingEvent != null && pingEvent.WaitOne(1000))
                            {
                                if (floatingBanner != null)
                                {
                                    floatingBanner.ShowMessage("🟢 Spacjomat już działa i czuwa w tle! (F7 = AI | F8 = Offline)");
                                }
                            }
                        }
                        catch { break; }
                    }
                });
                pingThread.IsBackground = true;
                pingThread.Start();
            }
            catch { }
            StartHotReloadWatcher();
        }

        // Watchdogi dzialaja zawsze - takze gdy hak nie zalozyl sie przy starcie (zostanie ponowiony).
        StartStabilityWatchdogs();
        try
        {
            string hkInfo = (hotkeyWindow != null) ? hotkeyWindow.Summary() : "brak";
            Log("Skroty systemowe: " + hkInfo);
            if (hotkeyWindow != null && hotkeyWindow.FailedCount() > 0 && floatingBanner != null)
            {
                floatingBanner.ShowMessage("⚠️ Czesc skrotow zajeta przez inny program - uzyj tez Ctrl+Shift+K / Ctrl+Shift+L");
            }
        }
        catch { }
    }

    private void StartHotReloadWatcher()
    {
        try
        {
            string csPath = Path.Combine(GetAppDir(), "Spacjomat.cs");
            if (!File.Exists(csPath)) return;
            // Auto-restart po zmianie pliku .cs to narzedzie dla programisty (wlaczane plikiem dev.flag
            // albo zmienna SPACJOMAT_DEV=1). U zwyklych uzytkownikow jest wylaczone, bo zly zapis pliku
            // w trakcie edycji potrafil wylaczyc dzialajaca aplikacje.
            if (Environment.GetEnvironmentVariable("SPACJOMAT_DEV") != "1" &&
                !File.Exists(Path.Combine(GetAppDir(), "dev.flag"))) return;
            startupCsWriteUtc = File.GetLastWriteTimeUtc(csPath);
            hotReloadTimer = new System.Windows.Forms.Timer();
            hotReloadTimer.Interval = 6000;
            hotReloadTimer.Tick += (s, e) =>
            {
                try
                {
                    if (!File.Exists(csPath)) return;
                    DateTime cur = File.GetLastWriteTimeUtc(csPath);
                    if (cur > startupCsWriteUtc.AddSeconds(2))
                    {
                        if (pendingReloadUtc == cur)
                        {
                            hotReloadTimer.Stop();
                            Log("Spacjomat.cs modified on disk — auto-restarting silently...");
                            RestartApplication();
                        }
                        else
                        {
                            pendingReloadUtc = cur;
                        }
                    }
                }
                catch { }
            };
            hotReloadTimer.Start();
        }
        catch { }
    }

    public void RestartApplication()
    {
        IntentionalExit = true;
        try { Environment.ExitCode = 99; } catch { }
        try
        {
            string vbsPath = Path.Combine(GetAppDir(), "Spacjomat.vbs");
            if (File.Exists(vbsPath))
            {
                var psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c ping -n 2 127.0.0.1 >nul & start \"\" wscript.exe \"" + vbsPath + "\"";
                psi.EnvironmentVariables["SPACJOMAT_SILENT"] = "1";
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch (Exception ex)
        {
            Log("RestartApplication error: " + ex.Message);
        }
        ExitThread();
    }

    public void OpenStudio(string initialText = null)
    {
        try
        {
            if (studioWindow == null || studioWindow.IsDisposed)
            {
                studioWindow = new SpacjomatStudioForm(appIcon, this);
            }

            if (!string.IsNullOrEmpty(initialText))
            {
                studioWindow.SetInputText(initialText);
            }

            studioWindow.Show();
            studioWindow.WindowState = FormWindowState.Normal;
            studioWindow.BringToFront();
            studioWindow.Activate();
        }
        catch (Exception ex)
        {
            Log("OpenStudio error: " + ex.Message);
        }
    }

    public void OpenAiSettings()
    {
        try
        {
            if (settingsWindow == null || settingsWindow.IsDisposed)
            {
                settingsWindow = new AiSettingsForm(appIcon, this);
            }
            settingsWindow.SyncFromConfig();
            settingsWindow.Show();
            settingsWindow.BringToFront();
            settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Log("OpenAiSettings error: " + ex.Message);
        }
    }

    public void RefreshUiAfterSettings()
    {
        if (pillMenuItem != null) pillMenuItem.Checked = aiConfig.ShowPill;
        if (suggestModeMenuItem != null) suggestModeMenuItem.Checked = (aiConfig.CorrectionMode != "auto");
        if (grammarlyPill != null)
        {
            grammarlyPill.ApplyConfig(aiConfig);
            if (aiConfig.ShowPill) grammarlyPill.Show();
            else grammarlyPill.Hide();
            grammarlyPill.RefreshStatus();
        }
        RefreshSettingsUsageView();
    }

    public void RefreshSettingsUsageView()
    {
        if (settingsWindow != null && !settingsWindow.IsDisposed && settingsWindow.Visible)
        {
            settingsWindow.RefreshUsageView();
        }
        if (grammarlyPill != null && !grammarlyPill.IsDisposed)
        {
            grammarlyPill.RefreshStatus();
        }
    }

    public void PreviewPill(AiConfig previewCfg)
    {
        if (grammarlyPill == null || previewCfg == null) return;
        grammarlyPill.ApplyConfig(previewCfg);
        if (previewCfg.ShowPill) grammarlyPill.Show();
        else grammarlyPill.Hide();
    }

    public void TogglePill()
    {
        aiConfig.ShowPill = !aiConfig.ShowPill;
        aiConfig.Save();
        RefreshUiAfterSettings();
    }

    public void ToggleSuggestMode()
    {
        aiConfig.CorrectionMode = (aiConfig.CorrectionMode == "auto") ? "suggest" : "auto";
        aiConfig.Save();
        RefreshUiAfterSettings();
        if (floatingBanner != null)
        {
            floatingBanner.ShowMessage(aiConfig.CorrectionMode == "suggest"
                ? "🎯 Tryb Grammarly: Pokazuj podpowiedzi do wyboru"
                : "⚡ Tryb Grammarly: Natychmiastowa auto-zamiana");
        }
    }

    public void ToggleState()
    {
        isEnabled = !isEnabled;
        if (statusMenuItem != null)
        {
            statusMenuItem.Text = isEnabled ? "Autokorekta w locie: WŁĄCZONA (F9 = Pauza)" : "Autokorekta w locie: WSTRZYMANA (F9 = Wznów)";
        }
        if (grammarlyPill != null) grammarlyPill.RefreshStatus();
        if (floatingBanner != null)
        {
            floatingBanner.ShowMessage(isEnabled ? "🟢 Autokorekta na Spacji WŁĄCZONA" : "🟠 Autokorekta na Spacji WSTRZYMANA (F9)");
        }
    }

    public void ToggleMode()
    {
        isAggressiveMode = !isAggressiveMode;
        if (modeMenuItem != null)
        {
            modeMenuItem.Text = isAggressiveMode ? "Tryb Offline: Głęboki / Fonetyczny" : "Tryb Offline: Bezpieczny (Zalecany)";
        }
        ShowBalloon("Spacjomat", isAggressiveMode ? "Włączono Tryb Głęboki." : "Włączono Tryb Bezpieczny (zalecany).");
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            if (floatingBanner != null && !string.IsNullOrEmpty(text))
            {
                string oneLine = text.Replace("\r", " ").Replace("\n", " • ");
                floatingBanner.ShowMessage("ℹ️ " + oneLine);
            }
        }
        catch { }
    }

    private void AddLastWordToWhitelist()
    {
        try
        {
            string w = !string.IsNullOrEmpty(lastOriginalWord) ? lastOriginalWord : currentWord.ToString();
            if (!string.IsNullOrEmpty(w))
            {
                string p = Path.Combine(GetAppDir(), "wlasne_slowa.txt");
                File.AppendAllText(p, "\n" + w.ToLower(), Encoding.UTF8);
                whitelist.Add(w.ToLower());
                ShowBalloon("Spacjomat", "Dodano do słownika: „" + w + "”");
            }
        }
        catch (Exception ex)
        {
            Log("AddLastWordToWhitelist error: " + ex.Message);
        }
    }

    private static void UndoLastCorrection()
    {
        try
        {
            if (lastWasAutocorrected && !string.IsNullOrEmpty(lastOriginalWord) && !string.IsNullOrEmpty(lastCorrectedWord))
            {
                int backspaces = lastCorrectedWord.Length + 1;
                SendAtomicReplacement(backspaces, lastOriginalWord, false, null);

                sessionIgnored.Add(lastOriginalWord.ToLower());
                previousLiveWord = lastOriginalWord;
                lastWasAutocorrected = false;
            }
        }
        catch (Exception ex)
        {
            Log("Undo error: " + ex.Message);
        }
    }

    private static void SendKeyInput(ushort vk, ushort scan, bool keyUp, bool extended)
    {
        INPUT[] inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki.wVk = vk;
        inputs[0].ki.wScan = scan;
        uint flags = keyUp ? KEYEVENTF_KEYUP : 0;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        inputs[0].ki.dwFlags = flags;
        SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void ReleaseAllModifiers()
    {
        ushort[] vks   = { 0xA0, 0xA1, 0x10, 0xA4, 0xA5, 0x12, 0xA2, 0xA3, 0x11 };
        ushort[] scans = { 0x2A, 0x36, 0x2A, 0x38, 0x38, 0x38, 0x1D, 0x1D, 0x1D };
        bool[]   ext   = { false, true, false, false, true, false, false, true, false };

        var batch = new List<INPUT>(9);
        for (int i = 0; i < vks.Length; i++)
        {
            if ((GetAsyncKeyState(vks[i]) & 0x8000) != 0)
            {
                INPUT inp = new INPUT();
                inp.type = INPUT_KEYBOARD;
                inp.ki.wVk = vks[i];
                inp.ki.wScan = scans[i];
                inp.ki.dwFlags = KEYEVENTF_KEYUP | (ext[i] ? KEYEVENTF_EXTENDEDKEY : 0u);
                batch.Add(inp);
            }
        }
        if (batch.Count > 0)
        {
            SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }

    private static void WaitForModifiersReleased(int maxWaitMs)
    {
        int[] keys = new int[] { 0x10, 0xA0, 0xA1, 0x11, 0xA2, 0xA3, 0x12, 0xA4, 0xA5 };
        int elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            bool anyDown = false;
            foreach (int k in keys)
            {
                if ((GetAsyncKeyState(k) & 0x8000) != 0)
                {
                    anyDown = true;
                    break;
                }
            }
            if (!anyDown) break;
            Thread.Sleep(8);
            elapsed += 8;
        }
    }

    private static void SimulateCtrlCombo(ushort vkKey, ushort scanKey)
    {
        isSimulatingInput = true;
        try
        {
            ReleaseAllModifiers();
            Thread.Sleep(6);

            SendKeyInput(0xA2, 0x1D, false, false); // LCtrl DOWN
            Thread.Sleep(10);

            SendKeyInput(vkKey, scanKey, false, false); // Key DOWN
            Thread.Sleep(8);

            SendKeyInput(vkKey, scanKey, true, false);  // Key UP
            Thread.Sleep(6);

            SendKeyInput(0xA2, 0x1D, true, false);  // LCtrl UP
        }
        finally
        {
            isSimulatingInput = false;
        }
    }

    private static string PollClipboardAfterCopy(uint seqBefore, int maxWaitMs)
    {
        int elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            Thread.Sleep(4);
            elapsed += 4;
            if (GetClipboardSequenceNumber() != seqBefore)
            {
                for (int r = 0; r < 4; r++)
                {
                    try
                    {
                        IDataObject iData = Clipboard.GetDataObject();
                        if (iData != null && iData.GetDataPresent(DataFormats.UnicodeText))
                        {
                            string txt = (string)iData.GetData(DataFormats.UnicodeText);
                            if (!string.IsNullOrEmpty(txt)) return txt;
                        }
                    }
                    catch { }
                    Thread.Sleep(4);
                }
                return null;
            }
        }
        return null;
    }

    private static void SimulateSelectAll() { SimulateCtrlCombo(0x41, 0x1E); } // Ctrl+A
    private static void SimulateCopy()      { SimulateCtrlCombo(0x43, 0x2E); } // Ctrl+C
    private static void SimulatePaste()     { SimulateCtrlCombo(0x56, 0x2F); } // Ctrl+V

    private static int correctionRunning = 0;

    private static void RestoreClipboardSafe(bool hadBackup, string backupText)
    {
        if (!hadBackup || backupText == null) return;
        try { Clipboard.SetDataObject(backupText, true, 4, 25); } catch { }
    }

    private static void PasteTextIntoTarget(string textToPaste, IntPtr targetHwnd, bool reselectAll, bool hadBackup, string backupText, string bannerMsg)
    {
        if (targetHwnd != IntPtr.Zero && GetForegroundWindow() != targetHwnd)
        {
            SetForegroundWindow(targetHwnd);
            Thread.Sleep(35);
        }

        if (reselectAll)
        {
            SimulateSelectAll();
            Thread.Sleep(30);
        }

        bool clipSet = false;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(textToPaste, true, 5, 25);
                clipSet = true;
                break;
            }
            catch { Thread.Sleep(15); }
        }

        if (!clipSet)
        {
            if (floatingBanner != null) floatingBanner.ShowMessage("⚠️ Schowek zajęty — spróbuj ponownie.");
            return;
        }

        Thread.Sleep(25);
        SimulatePaste();

        if (floatingBanner != null && !string.IsNullOrEmpty(bannerMsg))
        {
            floatingBanner.ShowMessage(bannerMsg);
        }

        if (hadBackup && backupText != null)
        {
            Thread.Sleep(220);
            RestoreClipboardSafe(true, backupText);
        }
    }

    public static void ApplySuggestedText(string chosenText, IntPtr targetHwnd, bool wasSelectAll)
    {
        Thread worker = new Thread(() =>
        {
            if (Interlocked.CompareExchange(ref correctionRunning, 1, 0) != 0) return;
            string backupText = null;
            bool hadBackup = false;
            try
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        backupText = Clipboard.GetText();
                        hadBackup = true;
                    }
                }
                catch { }

                PasteTextIntoTarget(chosenText, targetHwnd, wasSelectAll, hadBackup, backupText, "✔ Wstawiono wybraną podpowiedź!");
            }
            catch (Exception ex)
            {
                Log("ApplySuggestedText error: " + ex.Message);
            }
            finally
            {
                isSimulatingInput = false;
                Interlocked.Exchange(ref correctionRunning, 0);
            }
        });
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static void TriggerInPlaceCorrection(bool useAi, bool selectAllFirst, bool autoSelectAllIfEmpty)
    {
        int nowTick = Environment.TickCount;
        if (useAi)
        {
            if (unchecked(nowTick - lastAiTriggerTick) < 320) return;
            lastAiTriggerTick = nowTick;
        }
        else
        {
            if (unchecked(nowTick - lastOfflineTriggerTick) < 320) return;
            lastOfflineTriggerTick = nowTick;
        }

        if (useAi && !AiCorrectionEngine.IsAiReady(aiConfig))
        {
            if (floatingBanner != null)
            {
                floatingBanner.ShowMessage("⚠️ AI wymaga klucza — wklej go w ⚙ (baza Offline działa bez klucza: F8 / Ctrl+Shift+K)");
            }
            return;
        }

        if (Interlocked.CompareExchange(ref correctionRunning, 1, 0) != 0)
        {
            int runningMs = unchecked(Environment.TickCount - correctionStartTick);
            if (runningMs > 22000)
            {
                Log("Stability: wymuszone zwolnienie zawieszonej korekty (" + runningMs + " ms).");
                Interlocked.Exchange(ref correctionRunning, 0);
                isSimulatingInput = false;
                if (Interlocked.CompareExchange(ref correctionRunning, 1, 0) != 0) return;
            }
            else
            {
                if (floatingBanner != null) floatingBanner.ShowMessage("⏳ Poprzednia korekta jeszcze trwa - chwileczkę...");
                return;
            }
        }

        correctionStartTick = Environment.TickCount;
        int myGen = Interlocked.Increment(ref correctionGen);
        IntPtr capturedTargetHwnd = ResolveTargetWindow();

        Thread worker = new Thread(() =>
        {
            string backupText = null;
            bool hadBackup = false;
            try
            {
                Log(string.Format("InPlaceCorrection: Trigger (useAi={0}, selectAll={1}, autoSelect={2}).", useAi, selectAllFirst, autoSelectAllIfEmpty));

                WaitForModifiersReleased(180);
                EnsureTargetForeground(capturedTargetHwnd);

                try
                {
                    if (Clipboard.ContainsText())
                    {
                        backupText = Clipboard.GetText();
                        hadBackup = true;
                    }
                }
                catch { }

                bool effectivelySelectedAll = selectAllFirst;
                if (selectAllFirst)
                {
                    SimulateSelectAll();
                    Thread.Sleep(24);
                }

                uint seq0 = GetClipboardSequenceNumber();
                SimulateCopy();
                string originalText = PollClipboardAfterCopy(seq0, selectAllFirst ? 280 : 120);

                if (string.IsNullOrEmpty(originalText))
                {
                    if (autoSelectAllIfEmpty && !selectAllFirst)
                    {
                        effectivelySelectedAll = true;
                        SimulateSelectAll();
                        Thread.Sleep(24);
                        uint seq1 = GetClipboardSequenceNumber();
                        SimulateCopy();
                        originalText = PollClipboardAfterCopy(seq1, 280);
                    }
                    else
                    {
                        Thread.Sleep(20);
                        if (selectAllFirst) { SimulateSelectAll(); Thread.Sleep(28); }
                        uint seq2 = GetClipboardSequenceNumber();
                        SimulateCopy();
                        originalText = PollClipboardAfterCopy(seq2, 320);
                    }
                }

                if (string.IsNullOrEmpty(originalText))
                {
                    Log("InPlaceCorrection: No selected text detected.");
                    RestoreClipboardSafe(hadBackup, backupText);
                    if (floatingBanner != null)
                    {
                        floatingBanner.ShowMessage("⚠️ Pole tekstowe jest puste lub zaznacz tekst przed użyciem skrótu!");
                    }
                    return;
                }

                Log(string.Format("InPlaceCorrection: Captured text ({0} chars).", originalText.Length));

                if (useAi)
                {
                    bool suggestMode = (aiConfig == null || aiConfig.CorrectionMode != "auto");
                    string aiErr = null;
                    long aiMs = 0;

                    if (floatingBanner != null)
                    {
                        floatingBanner.ShowMessage(suggestMode
                            ? "🧠 AI przygotowuje podpowiedzi..."
                            : "🧠 AI poprawia cały tekst...");
                    }

                    if (suggestMode && suggestionPopup != null)
                    {
                        AiSuggestionResult sug = AiCorrectionEngine.SuggestWithAi(originalText, aiConfig, out aiErr, out aiMs);
                        if (Instance != null && grammarlyPill != null)
                        {
                            try { grammarlyPill.BeginInvoke(new Action(() => Instance.RefreshSettingsUsageView())); } catch { }
                        }

                        if (sug != null)
                        {
                            RestoreClipboardSafe(hadBackup, backupText);
                            if (sug.Options.Count > 0)
                            {
                                Log(string.Format("InPlaceCorrection: AI suggest success ({0} options) in {1} ms.", sug.Options.Count, aiMs));
                                LearnSafeTyposFromAiAsync(originalText, sug.Options[0].Text);
                                suggestionPopup.ShowSuggestions(originalText, sug, aiMs, capturedTargetHwnd, effectivelySelectedAll);
                                return;
                            }
                            if (!sug.HasErrors)
                            {
                                if (floatingBanner != null) floatingBanner.ShowMessage(string.Format("🧠 AI ({0} ms): Tekst jest już w 100% poprawny!", aiMs));
                                return;
                            }
                        }
                    }
                    else
                    {
                        string aiAuto = AiCorrectionEngine.CorrectWithAi(originalText, aiConfig, out aiErr, out aiMs);
                        if (Instance != null && grammarlyPill != null)
                        {
                            try { grammarlyPill.BeginInvoke(new Action(() => Instance.RefreshSettingsUsageView())); } catch { }
                        }
                        if (!string.IsNullOrEmpty(aiAuto))
                        {
                            Log(string.Format("InPlaceCorrection: AI success in {0} ms.", aiMs));
                            if (aiAuto.Equals(originalText, StringComparison.Ordinal))
                            {
                                RestoreClipboardSafe(hadBackup, backupText);
                                if (floatingBanner != null) floatingBanner.ShowMessage(string.Format("🧠 AI ({0} ms): Tekst jest już w 100% poprawny!", aiMs));
                                return;
                            }
                            LearnSafeTyposFromAiAsync(originalText, aiAuto);
                            PasteTextIntoTarget(aiAuto, IntPtr.Zero, false, hadBackup, backupText, string.Format("🧠 AI Poprawiono ({0} ms)", aiMs));
                            return;
                        }
                    }

                    Log("InPlaceCorrection: AI failed (" + (aiErr ?? "nieznany") + ")");
                    RestoreClipboardSafe(hadBackup, backupText);
                    if (floatingBanner != null)
                    {
                        string shortErr = string.IsNullOrEmpty(aiErr) ? "nieznany błąd" : (aiErr.Length > 68 ? aiErr.Substring(0, 65) + "..." : aiErr);
                        floatingBanner.ShowMessage("⚠️ AI (" + shortErr + ") — szybka baza: F8");
                    }
                    return;
                }

                // Pure fast Offline Database mode (F8 / Ctrl+Shift+K)
                var swOff = System.Diagnostics.Stopwatch.StartNew();
                int wordsFixed = 0, commasAdded = 0, sentencesCapitalized = 0;
                string offlineText = CorrectFullTextStructure(originalText, out wordsFixed, out commasAdded, out sentencesCapitalized);
                swOff.Stop();

                if (offlineText.Equals(originalText, StringComparison.Ordinal))
                {
                    RestoreClipboardSafe(hadBackup, backupText);
                    if (floatingBanner != null)
                    {
                        floatingBanner.ShowMessage(string.Format("⚡ Baza Offline ({0} ms): Brak prostych błędów!", swOff.ElapsedMilliseconds));
                    }
                    return;
                }

                PasteTextIntoTarget(
                    offlineText,
                    IntPtr.Zero,
                    false,
                    hadBackup,
                    backupText,
                    string.Format("⚡ Baza Offline ({0} ms): {1} słów | +{2} przecinków | {3} zdań",
                        swOff.ElapsedMilliseconds, wordsFixed, commasAdded, sentencesCapitalized));
            }
            catch (Exception ex)
            {
                Log("InPlaceCorrection error: " + ex.Message);
            }
            finally
            {
                if (Thread.VolatileRead(ref correctionGen) == myGen)
                {
                    isSimulatingInput = false;
                    Interlocked.Exchange(ref correctionRunning, 0);
                }
            }
        });

        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    public static void LoadAllDictionaries()
    {
        try
        {
            string baseDir = GetAppDir();
            directRules.Clear();
            bigramRules.Clear();
            whitelist.Clear();
            dictionary.Clear();
            strippedDict.Clear();
            wordsByLenAndChar.Clear();
            topWordsByLength.Clear();
            learnedRulesCount = 0;

            // 1. Binary 4.5M SJP valid word hashes (.bin 64-bit or compact .d48 <20MB)
            string sjpBinPath = Path.Combine(baseDir, "sjp_hashes.bin");
            string sjpD48Path = Path.Combine(baseDir, "sjp_hashes.d48");
            if (File.Exists(sjpBinPath))
            {
                byte[] bytes = File.ReadAllBytes(sjpBinPath);
                int count = bytes.Length / 8;
                ulong[] arr = new ulong[count];
                Buffer.BlockCopy(bytes, 0, arr, 0, count * 8);
                sjpHashes = arr;
                sjpIs48Bit = false;
            }
            else if (File.Exists(sjpD48Path))
            {
                byte[] dbytes = File.ReadAllBytes(sjpD48Path);
                int count = BitConverter.ToInt32(dbytes, 0);
                ulong[] arr = new ulong[count];
                int pos = 4;
                ulong prev = 0;
                for (int i = 0; i < count; i++)
                {
                    ulong delta = 0;
                    int shift = 0;
                    while (true)
                    {
                        byte b = dbytes[pos++];
                        delta |= ((ulong)(b & 0x7F)) << shift;
                        if ((b & 0x80) == 0) break;
                        shift += 7;
                    }
                    prev += delta;
                    arr[i] = prev;
                }
                sjpHashes = arr;
                sjpIs48Bit = true;
            }
            else
            {
                sjpHashes = new ulong[0];
                sjpIs48Bit = false;
            }

            // 2. Clean Direct Rules (reguly.txt) + Self-learned AI rules (nauczone_reguly.txt)
            Action<string, bool> loadRulesFile = (path, isLearned) =>
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var trim = line.Trim();
                    if (string.IsNullOrEmpty(trim) || trim.StartsWith("#")) continue;
                    var eq = trim.IndexOf('=');
                    if (eq > 0)
                    {
                        string from = trim.Substring(0, eq).Trim().ToLower();
                        string to = trim.Substring(eq + 1).Trim();
                        if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                        {
                            directRules[from] = to;
                            if (isLearned) learnedRulesCount++;
                        }
                    }
                }
            };
            loadRulesFile(Path.Combine(baseDir, "reguly.txt"), false);
            loadRulesFile(Path.Combine(baseDir, "nauczone_reguly.txt"), true);
            if (!directRules.ContainsKey("sie")) directRules["sie"] = "się";
            if (!directRules.ContainsKey("tyldo")) directRules["tyldo"] = "tylko";

            // 3. Context Bigrams (bigramy.txt)
            string bigramPath = Path.Combine(baseDir, "bigramy.txt");
            if (File.Exists(bigramPath))
            {
                foreach (var line in File.ReadAllLines(bigramPath, Encoding.UTF8))
                {
                    var trim = line.Trim();
                    if (string.IsNullOrEmpty(trim) || trim.StartsWith("#")) continue;
                    var eq = trim.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = trim.Substring(0, eq).Trim().ToLower();
                        string val = trim.Substring(eq + 1).Trim().ToLower();
                        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(val))
                        {
                            bigramRules[key] = val;
                        }
                    }
                }
            }

            // 4. Whitelist (wlasne_slowa.txt)
            string whitePath = Path.Combine(baseDir, "wlasne_slowa.txt");
            if (File.Exists(whitePath))
            {
                foreach (var line in File.ReadAllLines(whitePath, Encoding.UTF8))
                {
                    var trim = line.Trim();
                    if (string.IsNullOrEmpty(trim) || trim.StartsWith("#")) continue;
                    whitelist.Add(trim.ToLower());
                }
            }

            // 5. Unified Tier-1 Frequency Dictionary (slowa_pl.txt) z indeksem (dlugosc, pierwszaLitera)
            string dictPath = Path.Combine(baseDir, "slowa_pl.txt");
            if (File.Exists(dictPath))
            {
                using (var reader = new StreamReader(dictPath, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int space = line.LastIndexOf(' ');
                        if (space > 0)
                        {
                            string word = line.Substring(0, space).Trim().ToLower();
                            int freq;
                            if (word.Length > 0 && int.TryParse(line.Substring(space + 1).Trim(), out freq))
                            {
                                dictionary[word] = freq;

                                // Rozszerzony prog freq >= 200 (~175K slow!) dzieki szybkiemu indeksowaniu wg pierwszej litery
                                if (freq >= 200)
                                {
                                    int len = word.Length;
                                    char firstBase = StripChar(word[0]);
                                    Dictionary<char, List<KeyValuePair<string, int>>> byChar;
                                    if (!wordsByLenAndChar.TryGetValue(len, out byChar))
                                    {
                                        byChar = new Dictionary<char, List<KeyValuePair<string, int>>>();
                                        wordsByLenAndChar[len] = byChar;
                                    }
                                    List<KeyValuePair<string, int>> bucket;
                                    if (!byChar.TryGetValue(firstBase, out bucket))
                                    {
                                        bucket = new List<KeyValuePair<string, int>>();
                                        byChar[firstBase] = bucket;
                                    }
                                    bucket.Add(new KeyValuePair<string, int>(word, freq));

                                    if (freq >= 580)
                                    {
                                        List<KeyValuePair<string, int>> topList;
                                        if (!topWordsByLength.TryGetValue(len, out topList))
                                        {
                                            topList = new List<KeyValuePair<string, int>>();
                                            topWordsByLength[len] = topList;
                                        }
                                        topList.Add(new KeyValuePair<string, int>(word, freq));
                                    }
                                }

                                string stripped = StripDiacritics(word);
                                List<string> list;
                                if (!strippedDict.TryGetValue(stripped, out list))
                                {
                                    list = new List<string>();
                                    strippedDict[stripped] = list;
                                }
                                list.Add(word);
                            }
                        }
                    }
                }
            }

            foreach (var lenKv in wordsByLenAndChar)
            {
                foreach (var charKv in lenKv.Value)
                {
                    charKv.Value.Sort((a, b) => b.Value.CompareTo(a.Value));
                }
            }
            foreach (var topKv in topWordsByLength)
            {
                topKv.Value.Sort((a, b) => b.Value.CompareTo(a.Value));
            }

            Log(string.Format("Loaded {0} rules ({1} learned), {2} bigrams, {3} whitelist, {4} tier1 words, {5} SJP hashes.",
                directRules.Count, learnedRulesCount, bigramRules.Count, whitelist.Count, dictionary.Count, sjpHashes.Length));

            // Zwolnij tymczasowy 36MB bufor LOH po wczytaniu sjp_hashes
            GC.Collect();
        }
        catch (Exception ex)
        {
            Log("LoadAllDictionaries error: " + ex.Message);
        }
    }

    private static readonly object learnLock = new object();

    public static void LearnSafeTyposFromAiAsync(string originalText, string correctedText)
    {
        if (string.IsNullOrWhiteSpace(originalText) || string.IsNullOrWhiteSpace(correctedText)) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var origMatches = Regex.Matches(originalText, @"\b[\p{L}]+\b");
                var corrMatches = Regex.Matches(correctedText, @"\b[\p{L}]+\b");
                if (origMatches.Count == 0 || origMatches.Count != corrMatches.Count) return;

                var newPairs = new List<KeyValuePair<string, string>>();
                for (int i = 0; i < origMatches.Count; i++)
                {
                    string o = origMatches[i].Value.ToLower();
                    string c = corrMatches[i].Value.ToLower();
                    if (o.Length < 4 || c.Length < 4 || o == c) continue;
                    if (directRules.ContainsKey(o) || whitelist.Contains(o)) continue;
                    if (IsValidPolishWord(o)) continue;
                    if (!IsValidPolishWord(c)) continue;

                    double dist = QwertyLevenshtein(o, c, 2.1);
                    if (dist > 0 && dist <= 2.05)
                    {
                        newPairs.Add(new KeyValuePair<string, string>(o, c));
                    }
                }

                if (newPairs.Count == 0) return;
                lock (learnLock)
                {
                    string lp = Path.Combine(GetAppDir(), "nauczone_reguly.txt");
                    bool existedBefore = File.Exists(lp);
                    var toAppend = new List<string>();
                    if (!existedBefore) toAppend.Add("# Słowa wyuczone automatycznie z korekt AI (F7)");
                    int addedCount = 0;
                    foreach (var kv in newPairs)
                    {
                        if (!directRules.ContainsKey(kv.Key))
                        {
                            directRules[kv.Key] = kv.Value;
                            learnedRulesCount++;
                            addedCount++;
                            toAppend.Add(kv.Key + "=" + kv.Value);
                        }
                    }
                    if (addedCount > 0)
                    {
                        File.AppendAllLines(lp, toAppend, new UTF8Encoding(false));
                        Log("Auto-learned " + addedCount + " rule(s) from F7 AI.");
                    }
                }
            }
            catch { }
        });
    }

    public static string StripDiacritics(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case 'ą': sb.Append('a'); break;
                case 'ć': sb.Append('c'); break;
                case 'ę': sb.Append('e'); break;
                case 'ł': sb.Append('l'); break;
                case 'ń': sb.Append('n'); break;
                case 'ó': sb.Append('o'); break;
                case 'ś': sb.Append('s'); break;
                case 'ź': case 'ż': sb.Append('z'); break;
                case 'Ą': sb.Append('A'); break;
                case 'Ć': sb.Append('C'); break;
                case 'Ę': sb.Append('E'); break;
                case 'Ł': sb.Append('L'); break;
                case 'Ń': sb.Append('N'); break;
                case 'Ó': sb.Append('O'); break;
                case 'Ś': sb.Append('S'); break;
                case 'Ź': case 'Ż': sb.Append('Z'); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public static bool HasPolishDiacritics(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
        {
            if (c == 'ą' || c == 'ć' || c == 'ę' || c == 'ł' || c == 'ń' ||
                c == 'ó' || c == 'ś' || c == 'ź' || c == 'ż' ||
                c == 'Ą' || c == 'Ć' || c == 'Ę' || c == 'Ł' || c == 'Ń' ||
                c == 'Ó' || c == 'Ś' || c == 'Ź' || c == 'Ż')
                return true;
        }
        return false;
    }

    private static string TryPhoneticCorrection(string w)
    {
        string[] substitutions = new string[] { "rz:ż", "ż:rz", "ch:h", "h:ch", "u:ó", "ó:u" };
        foreach (var sub in substitutions)
        {
            var parts = sub.Split(':');
            if (w.Contains(parts[0]))
            {
                string cand = w.Replace(parts[0], parts[1]);
                int f = 0;
                if (dictionary.TryGetValue(cand, out f) && f >= 380) return cand;
            }
        }

        if (w.EndsWith("om") && w.Length >= 4)
        {
            string cand = w.Substring(0, w.Length - 2) + "ą";
            int f = 0;
            if (dictionary.TryGetValue(cand, out f) && f >= 380) return cand;
        }
        if (w.EndsWith("em") && w.Length >= 4)
        {
            string cand = w.Substring(0, w.Length - 2) + "ę";
            int f = 0;
            if (dictionary.TryGetValue(cand, out f) && f >= 380) return cand;
        }

        return null;
    }

    public static string MatchCasing(string original, string corrected)
    {
        if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(corrected)) return corrected;
        if (char.IsUpper(original[0]))
        {
            if (original.Length > 1)
            {
                bool allUpper = true;
                foreach (char c in original) if (char.IsLower(c)) { allUpper = false; break; }
                if (allUpper) return corrected.ToUpper();
            }
            return char.ToUpper(corrected[0]) + (corrected.Length > 1 ? corrected.Substring(1) : "");
        }
        return corrected;
    }

    private static readonly bool[,] isAdjacentKey = new bool[128, 128];

    static SpacjomatContext()
    {
        string[] rows = new string[] {
            "qwertyuiop",
            "asdfghjkl",
            "zxcvbnm"
        };
        for (int r = 0; r < rows.Length; r++)
        {
            string row = rows[r];
            for (int c = 0; c < row.Length; c++)
            {
                char ch = row[c];
                if (c > 0) SetAdjacent(ch, row[c - 1]);
                if (c < row.Length - 1) SetAdjacent(ch, row[c + 1]);

                if (r > 0)
                {
                    string prevRow = rows[r - 1];
                    if (c < prevRow.Length) SetAdjacent(ch, prevRow[c]);
                    if (c + 1 < prevRow.Length) SetAdjacent(ch, prevRow[c + 1]);
                    if (c > 0 && c - 1 < prevRow.Length) SetAdjacent(ch, prevRow[c - 1]);
                }
                if (r < rows.Length - 1)
                {
                    string nextRow = rows[r + 1];
                    if (c < nextRow.Length) SetAdjacent(ch, nextRow[c]);
                    if (c + 1 < nextRow.Length) SetAdjacent(ch, nextRow[c + 1]);
                    if (c > 0 && c - 1 < nextRow.Length) SetAdjacent(ch, nextRow[c - 1]);
                }
            }
        }
    }

    private static void SetAdjacent(char a, char b)
    {
        if (a < 128 && b < 128)
        {
            isAdjacentKey[a, b] = true;
            isAdjacentKey[b, a] = true;
        }
    }

    private static char StripChar(char c)
    {
        switch (c)
        {
            case 'ą': return 'a';
            case 'ć': return 'c';
            case 'ę': return 'e';
            case 'ł': return 'l';
            case 'ń': return 'n';
            case 'ó': return 'o';
            case 'ś': return 's';
            case 'ź': case 'ż': return 'z';
            default: return c;
        }
    }

    [ThreadStatic] private static double[] qPrevPrev;
    [ThreadStatic] private static double[] qPrev;
    [ThreadStatic] private static double[] qCurr;

    private static double QwertyLevenshtein(string source, string target, double maxAllowed)
    {
        if (string.IsNullOrEmpty(source)) return string.IsNullOrEmpty(target) ? 0 : target.Length;
        if (string.IsNullOrEmpty(target)) return source.Length;

        int sLen = source.Length;
        int tLen = target.Length;
        if (sLen > 60 || tLen > 60) return 99.0;

        if (qCurr == null)
        {
            qPrevPrev = new double[65];
            qPrev = new double[65];
            qCurr = new double[65];
        }

        for (int j = 0; j <= tLen; j++) qPrev[j] = j;
        double prevRowMin = 0;

        for (int i = 1; i <= sLen; i++)
        {
            qCurr[0] = i;
            double rowMin = i;
            char sc = source[i - 1];
            char scBase = StripChar(sc);

            for (int j = 1; j <= tLen; j++)
            {
                char tc = target[j - 1];
                char tcBase = StripChar(tc);

                double cost;
                if (sc == tc) cost = 0;
                else if (scBase == tcBase) cost = 0.2;
                else if (sc < 128 && tc < 128 && isAdjacentKey[sc, tc]) cost = 0.65;
                else cost = 1.0;

                double insDel = Math.Min(qPrev[j] + 1.0, qCurr[j - 1] + 1.0);
                double best = Math.Min(insDel, qPrev[j - 1] + cost);

                if (i > 1 && j > 1 && source[i - 1] == target[j - 2] && source[i - 2] == target[j - 1])
                {
                    double trans = qPrevPrev[j - 2] + 0.7;
                    if (trans < best) best = trans;
                }

                qCurr[j] = best;
                if (best < rowMin) rowMin = best;
            }

            if (i > 1 && rowMin > maxAllowed && prevRowMin > maxAllowed)
            {
                return 99.0;
            }

            double[] tmp = qPrevPrev;
            qPrevPrev = qPrev;
            qPrev = qCurr;
            qCurr = tmp;
            prevRowMin = rowMin;
        }

        return qPrev[tLen];
    }

    public static string FindWordCorrection(string input) { return FindWordCorrection(input, "", false); }
    public static string FindWordCorrection(string input, bool isLiveTyping) { return FindWordCorrection(input, "", isLiveTyping); }

    public static string FindWordCorrection(string input, string prevWord, bool isLiveTyping)
    {
        if (string.IsNullOrEmpty(input) || input.Length < 2) return null;
        string lower = input.ToLower();

        // 1. Whitelist & Ignored
        if (whitelist.Contains(lower) || sessionIgnored.Contains(lower))
        {
            return null;
        }

        // 2. Context Bigram Check FIRST (disambiguates "w sprawie", "problem ze", "ta część", "bardzo źle")
        if (!string.IsNullOrEmpty(prevWord))
        {
            string biKey = prevWord.ToLower() + "|" + lower;
            string biMatch;
            if (bigramRules.TryGetValue(biKey, out biMatch))
            {
                if (biMatch.Equals(lower, StringComparison.OrdinalIgnoreCase)) return null;
                return MatchCasing(input, biMatch);
            }
        }

        // 3. Direct verified typo rules
        string ruleMatch;
        if (directRules.TryGetValue(lower, out ruleMatch))
        {
            if (ruleMatch.Equals(input, StringComparison.OrdinalIgnoreCase)) return null;
            return MatchCasing(input, ruleMatch);
        }

        // 4. Full 4.5M SJP + Tier-1 VALID POLISH WORD PROTECTION
        if (IsValidPolishWord(lower))
        {
            // Never alter valid words that already contain Polish diacritics
            if (HasPolishDiacritics(lower))
            {
                return null;
            }

            // If pure ASCII valid Polish word (e.g. "nadal", "sama", "sprawie", "okej", "milionom")
            if (ambiguousKeepAscii.Contains(lower))
            {
                return null;
            }

            string stripped = StripDiacritics(lower);
            List<string> candList;
            if (strippedDict.TryGetValue(stripped, out candList))
            {
                string bestDiacritic = null;
                int maxScore = -1;
                foreach (var c in candList)
                {
                    if (HasPolishDiacritics(c))
                    {
                        int f = 0;
                        dictionary.TryGetValue(c, out f);
                        if (f > maxScore) { maxScore = f; bestDiacritic = c; }
                    }
                }
                if (bestDiacritic != null)
                {
                    if (lower.EndsWith("a") && bestDiacritic.EndsWith("ą")) return null;
                    if (lower.EndsWith("e") && bestDiacritic.EndsWith("ę")) return null;

                    int currentScore = 0;
                    dictionary.TryGetValue(lower, out currentScore);
                    if (maxScore - currentScore >= 300)
                    {
                        return MatchCasing(input, bestDiacritic);
                    }
                }
            }
            return null;
        }

        // 5. Missing diacritics for unknown ASCII word
        string strippedUnknown = StripDiacritics(lower);
        List<string> diacCandidates;
        if (strippedDict.TryGetValue(strippedUnknown, out diacCandidates))
        {
            string best = null;
            int maxF = -1;
            foreach (var c in diacCandidates)
            {
                int f = 0;
                dictionary.TryGetValue(c, out f);
                if (f > maxF) { maxF = f; best = c; }
            }
            if (best != null && !best.Equals(lower, StringComparison.OrdinalIgnoreCase))
            {
                return MatchCasing(input, best);
            }
        }

        // 5b. Clean suffix typo normalization (-cjia -> -cja, np. aplikacjia -> aplikacja)
        if (lower.EndsWith("cjia") && lower.Length >= 6)
        {
            string cja = lower.Substring(0, lower.Length - 4) + "cja";
            if (IsValidPolishWord(cja)) return MatchCasing(input, cja);
        }

        // 6. Glued words separator (np. bardzoszybko -> bardzo szybko, nielubie -> nie lubie, wsumie -> w sumie)
        if (lower.Length >= 5)
        {
            string[] shortPrefixes = new string[] {
                "nie","dla","bez","pod","nad","jak","czy","tak","juz","już","ale",
                "na","po","za","do","od","we","ze","bo","to","co","by","ja","ty","my","wy","on","w","z","o","u"
            };
            foreach (string pref in shortPrefixes)
            {
                if (lower.Length > pref.Length + 2 && lower.StartsWith(pref, StringComparison.Ordinal))
                {
                    string p2 = lower.Substring(pref.Length);
                    if (p2 == "lem" || p2 == "bym" || p2 == "bys" || p2 == "smy" || p2 == "scie" || p2 == "cie" || p2 == "kolwiek" || p2 == "indziej") continue;
                    string c1 = directRules.ContainsKey(pref) ? directRules[pref] : pref;
                    string c2 = ResolveGluedPart(p2, 460);
                    if (c2 != null)
                    {
                        return MatchCasing(input, c1 + " " + c2);
                    }
                }
            }
        }

        if (lower.Length >= 8)
        {
            for (int splitIdx = 4; splitIdx <= lower.Length - 4; splitIdx++)
            {
                string p1 = lower.Substring(0, splitIdx);
                string p2 = lower.Substring(splitIdx);

                if (p2 == "lem" || p2 == "bym" || p2 == "bys" || p2 == "smy" || p2 == "scie" || p2 == "cie") continue;

                string c1 = ResolveGluedPart(p1, 480);
                string c2 = ResolveGluedPart(p2, 480);
                if (c1 != null && c2 != null)
                {
                    return MatchCasing(input, c1 + " " + c2);
                }
            }
        }

        // 7. Polish phonetic mutation (rz/ż, ch/h, ó/u, ą/om, ę/em)
        string phoneticFix = TryPhoneticCorrection(lower);
        if (phoneticFix != null)
        {
            return MatchCasing(input, phoneticFix);
        }

        // 8. Szybkie indeksowane wyszukiwanie Fuzzy wg (dlugosc, pierwszaLitera)
        int inputLen = lower.Length;
        if (inputLen <= 3 || inputLen > 28) return null;

        double maxAllowedDist = (inputLen <= 5 && !isAggressiveMode) ? 1.15 : (isAggressiveMode ? 2.0 : 1.45);
        string bestTypo = null;
        double bestScore = double.MinValue;
        char firstStripped = strippedUnknown[0];

        var candidateChars = new List<char>(6);
        candidateChars.Add(firstStripped);
        if (firstStripped < 128)
        {
            for (char ch = 'a'; ch <= 'z'; ch++)
            {
                if (ch != firstStripped && isAdjacentKey[firstStripped, ch]) candidateChars.Add(ch);
            }
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int maxBudgetMs = isLiveTyping ? 6 : 60;

        Action<string, int> evalCandidate = (dictWord, freq) =>
        {
            double dist = QwertyLevenshtein(lower, dictWord, maxAllowedDist);
            if (dist > 0 && dist <= maxAllowedDist)
            {
                bool confident = (dist <= 0.85 && freq >= 200) || (dist <= 1.15 && freq >= 270) || (freq >= 420);
                if (!confident) return;
                double score = freq - (dist * 520.0);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTypo = dictWord;
                }
            }
        };

        for (int l = Math.Max(3, inputLen - 1); l <= inputLen + 1; l++)
        {
            Dictionary<char, List<KeyValuePair<string, int>>> byChar;
            if (!wordsByLenAndChar.TryGetValue(l, out byChar)) continue;

            foreach (char cc in candidateChars)
            {
                List<KeyValuePair<string, int>> list;
                if (!byChar.TryGetValue(cc, out list)) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    if ((i & 127) == 0 && sw.ElapsedMilliseconds > maxBudgetMs) break;
                    evalCandidate(list[i].Key, list[i].Value);
                }
                if (sw.ElapsedMilliseconds > maxBudgetMs) break;
            }
            if (sw.ElapsedMilliseconds > maxBudgetMs) break;
        }

        // Sprawdz pominiecie pierwszej litery/polskiego znaku (np. wietnie -> swietnie, rzestaje -> przestaje) wsrod czestych slow
        if (sw.ElapsedMilliseconds <= maxBudgetMs && inputLen >= 5)
        {
            List<KeyValuePair<string, int>> longerTop;
            if (topWordsByLength.TryGetValue(inputLen + 1, out longerTop))
            {
                int lim = Math.Min(longerTop.Count, 450);
                for (int i = 0; i < lim; i++)
                {
                    string dw = longerTop[i].Key;
                    if (StripChar(dw[1]) != firstStripped) continue;
                    double dist = QwertyLevenshtein(lower, dw, 1.05);
                    if (dist > 0 && dist <= 1.05)
                    {
                        double sc = longerTop[i].Value - (dist * 450.0);
                        if (sc > bestScore)
                        {
                            bestScore = sc;
                            bestTypo = dw;
                        }
                    }
                }
            }
        }

        if (bestTypo != null)
        {
            return MatchCasing(input, bestTypo);
        }

        return null;
    }

    private static string ResolveGluedPart(string part, int minFreq)
    {
        string rm;
        if (directRules.TryGetValue(part, out rm) && rm.IndexOf(' ') < 0) return rm;
        int f = 0;
        if (dictionary.TryGetValue(part, out f) && f >= minFreq) return part;

        string st = StripDiacritics(part);
        List<string> cands;
        if (strippedDict.TryGetValue(st, out cands))
        {
            string best = null;
            int bestF = 0;
            foreach (string c in cands)
            {
                int cf = 0;
                if (dictionary.TryGetValue(c, out cf) && cf >= minFreq && cf > bestF)
                {
                    bestF = cf;
                    best = c;
                }
            }
            if (best != null) return best;
        }
        return null;
    }

    public static string CorrectFullTextStructure(string input, out int wordsFixed, out int commasAdded, out int sentencesCapitalized)
    {
        wordsFixed = 0;
        commasAdded = 0;
        sentencesCapitalized = 0;
        if (string.IsNullOrWhiteSpace(input)) return input;

        var protectedTokens = new List<string>();
        string s = Regex.Replace(input, @"(https?://\S+|www\.\S+|[A-Za-z]:\\\S+|\S+@\S+\.\S+)", m =>
        {
            string key = "__SPACJOMATPROT" + protectedTokens.Count + "__";
            protectedTokens.Add(m.Value);
            return key;
        });

        s = Regex.Replace(s, @"\s+([,\.\?!;:])", "$1");
        s = Regex.Replace(s, @"([,\.\?!;:])([\p{L}])", "$1 $2");
        s = Regex.Replace(s, @"[ \t]{2,}", " ");

        var matches = Regex.Matches(s, @"(\b[\p{L}\d_]+\b|[^\p{L}\d_]+)");
        List<string> tokens = new List<string>();
        foreach (Match m in matches) tokens.Add(m.Value);

        // Step A: Multi-word phrase replacements (2-3 words)
        for (int i = 0; i < tokens.Count - 2; i++)
        {
            if (tokens[i + 1] == " ")
            {
                if (i + 4 < tokens.Count && tokens[i + 3] == " ")
                {
                    string phrase3 = (tokens[i] + " " + tokens[i + 2] + " " + tokens[i + 4]).ToLower();
                    string p3Corr;
                    if (directRules.TryGetValue(phrase3, out p3Corr) && !p3Corr.Equals(phrase3, StringComparison.OrdinalIgnoreCase))
                    {
                        tokens[i] = MatchCasing(tokens[i], p3Corr);
                        tokens.RemoveAt(i + 4); tokens.RemoveAt(i + 3);
                        tokens.RemoveAt(i + 2); tokens.RemoveAt(i + 1);
                        wordsFixed++;
                        i--;
                        continue;
                    }
                }

                string phrase2 = (tokens[i] + " " + tokens[i + 2]).ToLower();
                string p2Corr;
                if (directRules.TryGetValue(phrase2, out p2Corr) && !p2Corr.Equals(phrase2, StringComparison.OrdinalIgnoreCase))
                {
                    tokens[i] = MatchCasing(tokens[i], p2Corr);
                    tokens.RemoveAt(i + 2);
                    tokens.RemoveAt(i + 1);
                    wordsFixed++;
                    i--;
                    continue;
                }

                // Przesunieta spacja (np. "n astale" -> "na stale", "ni ewiem" -> "nie wiem")
                string tLeft = tokens[i].ToLower();
                string tRight = tokens[i + 2].ToLower();
                if (tLeft.Length >= 1 && tLeft.Length <= 2 && tRight.Length >= 4 &&
                    !IsValidPolishWord(tRight) && !whitelist.Contains(tRight) && !directRules.ContainsKey(tRight))
                {
                    string newLeft = tLeft + tRight[0];
                    string newRight = tRight.Substring(1);
                    if (newLeft == "na" || newLeft == "nie" || newLeft == "do" || newLeft == "dla" || newLeft == "czy" ||
                        newLeft == "po" || newLeft == "za" || newLeft == "od" || newLeft == "we" || newLeft == "ze" ||
                        newLeft == "to" || newLeft == "co" || newLeft == "ma")
                    {
                        string validRight = ResolveGluedPart(newRight, 420);
                        if (validRight != null)
                        {
                            tokens[i] = MatchCasing(tokens[i], newLeft);
                            tokens[i + 2] = MatchCasing(tokens[i + 2], validRight);
                            wordsFixed++;
                        }
                    }
                }
            }
        }

        // Step B: Context-aware single word corrections
        string prevWord = "";
        for (int i = 0; i < tokens.Count; i++)
        {
            string tok = tokens[i];
            if (tok.StartsWith("__SPACJOMATPROT")) { prevWord = ""; continue; }
            if (Regex.IsMatch(tok, @"^[\p{L}]+$"))
            {
                if (tok.Length >= 2)
                {
                    string corr = FindWordCorrection(tok, prevWord, false);
                    if (!string.IsNullOrEmpty(corr) && !corr.Equals(tok, StringComparison.Ordinal))
                    {
                        tokens[i] = corr;
                        wordsFixed++;
                    }
                }
                prevWord = tokens[i];
            }
            else if (tok.IndexOfAny(new char[] { '.', '!', '?', '\n', ',', ';', ':' }) >= 0)
            {
                prevWord = "";
            }
        }

        // Step C: Smart commas before conjunctions
        for (int i = 0; i < tokens.Count; i++)
        {
            string cur = tokens[i];
            if (commaConjunctions.Contains(cur))
            {
                int prevWordIdx = -1;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (Regex.IsMatch(tokens[j], @"^[\p{L}\d_]+$"))
                    {
                        prevWordIdx = j;
                        break;
                    }
                    if (tokens[j].IndexOfAny(new char[] { '.', '!', '?', ',', ';', ':', '\n' }) >= 0)
                    {
                        break;
                    }
                }

                if (prevWordIdx != -1)
                {
                    bool hasComma = false;
                    for (int k = prevWordIdx + 1; k < i; k++)
                    {
                        if (tokens[k].Contains(",")) { hasComma = true; break; }
                    }

                    if (!hasComma)
                    {
                        tokens[prevWordIdx] = tokens[prevWordIdx] + ",";
                        commasAdded++;
                    }
                }
            }
        }

        string combined = string.Join("", tokens.ToArray());
        combined = CapitalizeSentences(combined, ref sentencesCapitalized);

        for (int i = 0; i < protectedTokens.Count; i++)
        {
            combined = combined.Replace("__SPACJOMATPROT" + i + "__", protectedTokens[i]);
        }

        return combined;
    }

    private static string CapitalizeSentences(string text, ref int count)
    {
        if (string.IsNullOrEmpty(text)) return text;
        StringBuilder sb = new StringBuilder(text);
        bool capitalizeNext = true;

        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            if (char.IsLetter(c))
            {
                if (capitalizeNext)
                {
                    if (char.IsLower(c))
                    {
                        sb[i] = char.ToUpper(c);
                        count++;
                    }
                    capitalizeNext = false;
                }
            }
            else if (c == '.' || c == '!' || c == '?' || c == '\n')
            {
                bool isAbbrev = false;
                if (c == '.' && i >= 3)
                {
                    string sub = sb.ToString(Math.Max(0, i - 4), Math.Min(5, i + 1));
                    foreach (var ab in abbreviations)
                    {
                        if (sub.EndsWith(ab, StringComparison.OrdinalIgnoreCase))
                        {
                            isAbbrev = true;
                            break;
                        }
                    }
                }

                if (!isAbbrev)
                {
                    capitalizeNext = true;
                }
            }
        }
        return sb.ToString();
    }

    private static uint SendAtomicReplacement(int backspaces, string text, bool sendRealSpace, char? trailingPunct)
    {
        int len = (backspaces > 0 ? backspaces : 0)
                + (string.IsNullOrEmpty(text) ? 0 : text.Length)
                + (sendRealSpace ? 1 : 0)
                + (trailingPunct.HasValue ? 1 : 0);
        if (len <= 0) return 0;

        INPUT[] inputs = new INPUT[len * 2];
        int idx = 0;

        for (int i = 0; i < backspaces; i++)
        {
            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = VK_BACK;
            inputs[idx].ki.wScan = 0x0E;
            inputs[idx].ki.dwFlags = 0;
            idx++;

            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = VK_BACK;
            inputs[idx].ki.wScan = 0x0E;
            inputs[idx].ki.dwFlags = KEYEVENTF_KEYUP;
            idx++;
        }

        if (!string.IsNullOrEmpty(text))
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].ki.wVk = 0;
                inputs[idx].ki.wScan = (ushort)c;
                inputs[idx].ki.dwFlags = KEYEVENTF_UNICODE;
                idx++;

                inputs[idx].type = INPUT_KEYBOARD;
                inputs[idx].ki.wVk = 0;
                inputs[idx].ki.wScan = (ushort)c;
                inputs[idx].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
                idx++;
            }
        }

        if (sendRealSpace)
        {
            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = VK_SPACE;
            inputs[idx].ki.wScan = 0x39;
            inputs[idx].ki.dwFlags = 0;
            idx++;

            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = VK_SPACE;
            inputs[idx].ki.wScan = 0x39;
            inputs[idx].ki.dwFlags = KEYEVENTF_KEYUP;
            idx++;
        }

        if (trailingPunct.HasValue)
        {
            char p = trailingPunct.Value;
            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = 0;
            inputs[idx].ki.wScan = (ushort)p;
            inputs[idx].ki.dwFlags = KEYEVENTF_UNICODE;
            idx++;

            inputs[idx].type = INPUT_KEYBOARD;
            inputs[idx].ki.wVk = 0;
            inputs[idx].ki.wScan = (ushort)p;
            inputs[idx].ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            idx++;
        }

        int structSize = Marshal.SizeOf(typeof(INPUT));
        isSimulatingInput = true;
        try
        {
            return SendInput((uint)inputs.Length, inputs, structSize);
        }
        finally
        {
            isSimulatingInput = false;
        }
    }

    private static bool IsPureModifierKey(int vk)
    {
        return vk == 0x10 || vk == 0x11 || vk == 0x12 ||
               (vk >= 0xA0 && vk <= 0xA5) ||
               vk == 0x5B || vk == 0x5C || vk == 0x5D ||
               vk == 0x14 || vk == 0x90 || vk == 0x91;
    }

    // Wrapper mierzacy czas callbacka: Windows usuwa hak, ktory odpowiada zbyt wolno (~300 ms).
    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int t0 = Environment.TickCount;
        IntPtr result = HookCallbackCore(nCode, wParam, lParam);
        int dt = unchecked(Environment.TickCount - t0);
        if (dt > 150)
        {
            slowHookCount++;
            Log("Stability: wolny callback haka: " + dt + " ms");
        }
        return result;
    }

    private static IntPtr HookCallbackCore(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                if (isSimulatingInput)
                {
                    return CallNextHookEx(hookId, nCode, wParam, lParam);
                }

                KBDLLHOOKSTRUCT hookStruct = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                if ((hookStruct.flags & LLKHF_INJECTED) != 0)
                {
                    return CallNextHookEx(hookId, nCode, wParam, lParam);
                }

                int msg = wParam.ToInt32();
                int vk = (int)hookStruct.vkCode;
                lastHookVk = vk;
                lastHookTick = Environment.TickCount;

                if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    if (vk == VK_K && suppressNextKUp) { suppressNextKUp = false; return (IntPtr)1; }
                    if (vk == 0x4C && suppressNextLUp) { suppressNextLUp = false; return (IntPtr)1; }
                    if (vk == VK_F7 && suppressNextF7Up) { suppressNextF7Up = false; return (IntPtr)1; }
                    if (vk == VK_F8 && suppressNextF8Up) { suppressNextF8Up = false; return (IntPtr)1; }
                    if (vk == VK_F9 && suppressNextF9Up) { suppressNextF9Up = false; return (IntPtr)1; }
                    return CallNextHookEx(hookId, nCode, wParam, lParam);
                }

                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (vk == VK_F9)
                    {
                        suppressNextF9Up = true;
                        ThreadPool.QueueUserWorkItem(_ => RunOnUi(() => { if (Instance != null) Instance.ToggleState(); }));
                        return (IntPtr)1;
                    }

                    if (vk == VK_F7)
                    {
                        suppressNextF7Up = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Log("Hook caught F7 -> AI (whole field)");
                            TriggerInPlaceCorrection(true, true, false);
                        });
                        return (IntPtr)1;
                    }

                    if (vk == VK_F8)
                    {
                        suppressNextF8Up = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Log("Hook caught F8 -> Fast Offline Database");
                            TriggerInPlaceCorrection(false, false, true);
                        });
                        return (IntPtr)1;
                    }

                    bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
                    bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                    bool leftAlt = (GetAsyncKeyState(0xA4) & 0x8000) != 0;
                    bool rightAlt = (GetAsyncKeyState(0xA5) & 0x8000) != 0;
                    bool winDown = ((GetAsyncKeyState(0x5B) & 0x8000) != 0) || ((GetAsyncKeyState(0x5C) & 0x8000) != 0);

                    if (ctrl && (shift || leftAlt) && vk == VK_K)
                    {
                        suppressNextKUp = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Log("Hook caught Ctrl+Shift/Alt+K -> Fast Offline Database");
                            TriggerInPlaceCorrection(false, false, true);
                        });
                        return (IntPtr)1;
                    }

                    if (ctrl && shift && vk == 0x4C)
                    {
                        suppressNextLUp = true;
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            Log("Hook caught Ctrl+Shift+L -> AI (whole field)");
                            TriggerInPlaceCorrection(true, true, false);
                        });
                        return (IntPtr)1;
                    }

                    if (!isEnabled)
                    {
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    if (IsPureModifierKey(vk))
                    {
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    IntPtr fgWnd = GetForegroundWindow();
                    uint fgThread = (fgWnd != IntPtr.Zero) ? GetWindowThreadProcessId(fgWnd, IntPtr.Zero) : 0;
                    IntPtr focusWnd = IntPtr.Zero;
                    if (fgThread != 0)
                    {
                        GUITHREADINFO gti = new GUITHREADINFO();
                        gti.cbSize = Marshal.SizeOf(typeof(GUITHREADINFO));
                        if (GetGUIThreadInfo(fgThread, ref gti)) focusWnd = gti.hwndFocus;
                    }
                    if (fgWnd != lastForegroundHwnd || focusWnd != lastFocusHwnd)
                    {
                        lastForegroundHwnd = fgWnd;
                        lastFocusHwnd = focusWnd;
                        currentWord.Clear();
                        previousLiveWord = "";
                        lastWasAutocorrected = false;
                    }

                    if (vk == VK_BACK)
                    {
                        if (lastWasAutocorrected)
                        {
                            UndoLastCorrection();
                            return (IntPtr)1;
                        }

                        if (currentWord.Length > 0) currentWord.Length--;
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    if (lastWasAutocorrected) lastWasAutocorrected = false;

                    if (vk == VK_SPACE)
                    {
                        if (ctrl || leftAlt || rightAlt || winDown)
                        {
                            currentWord.Clear();
                            previousLiveWord = "";
                            return CallNextHookEx(hookId, nCode, wParam, lParam);
                        }

                        string word = currentWord.ToString();
                        currentWord.Clear();

                        if (word.Length >= 2)
                        {
                            string correction = FindWordCorrection(word, previousLiveWord, true);
                            if (!string.IsNullOrEmpty(correction) && !correction.Equals(word, StringComparison.Ordinal))
                            {
                                uint sent = SendAtomicReplacement(word.Length, correction, true, null);
                                if (sent > 0)
                                {
                                    lastOriginalWord = word;
                                    lastCorrectedWord = correction;
                                    previousLiveWord = correction;
                                    lastWasAutocorrected = true;
                                    return (IntPtr)1;
                                }
                            }
                            previousLiveWord = word;
                        }
                        else
                        {
                            previousLiveWord = word;
                        }
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    if (vk == VK_RETURN || vk == VK_TAB || vk == VK_ESCAPE || vk == VK_DELETE || vk == 0x2D || (vk >= 0x21 && vk <= 0x28))
                    {
                        currentWord.Clear();
                        previousLiveWord = "";
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    if ((ctrl && !rightAlt) || leftAlt || winDown)
                    {
                        currentWord.Clear();
                        previousLiveWord = "";
                        return CallNextHookEx(hookId, nCode, wParam, lParam);
                    }

                    Array.Clear(hookKeyState, 0, 256);
                    if ((GetAsyncKeyState(0xA0) & 0x8000) != 0) hookKeyState[0xA0] = 0x80;
                    if ((GetAsyncKeyState(0xA1) & 0x8000) != 0) hookKeyState[0xA1] = 0x80;
                    if (shift || hookKeyState[0xA0] != 0 || hookKeyState[0xA1] != 0) hookKeyState[0x10] = 0x80;
                    if ((GetAsyncKeyState(0xA2) & 0x8000) != 0) hookKeyState[0xA2] = 0x80;
                    if ((GetAsyncKeyState(0xA3) & 0x8000) != 0) hookKeyState[0xA3] = 0x80;
                    if (ctrl || hookKeyState[0xA2] != 0 || hookKeyState[0xA3] != 0) hookKeyState[0x11] = 0x80;
                    if (leftAlt) hookKeyState[0xA4] = 0x80;
                    if (rightAlt) hookKeyState[0xA5] = 0x80;
                    if (leftAlt || rightAlt) hookKeyState[0x12] = 0x80;
                    if (rightAlt) { hookKeyState[0x11] |= 0x80; hookKeyState[0x12] |= 0x80; }
                    if ((GetKeyState(0x14) & 1) != 0) hookKeyState[0x14] = 1;

                    IntPtr hkl = (fgThread != 0) ? GetKeyboardLayout(fgThread) : IntPtr.Zero;
                    hookCharBuffer.Clear();
                    int count = ToUnicodeEx((uint)vk, hookStruct.scanCode, hookKeyState, hookCharBuffer, hookCharBuffer.Capacity, 4, hkl);

                    if (count > 0)
                    {
                        string str = hookCharBuffer.ToString(0, Math.Min(count, hookCharBuffer.Length));
                        foreach (char c in str)
                        {
                            if (char.IsLetter(c))
                            {
                                if (currentWord.Length < 48) currentWord.Append(c);
                            }
                            else if (c == '.' || c == ',' || c == '!' || c == '?')
                            {
                                string word = currentWord.ToString();
                                currentWord.Clear();

                                if (word.Length >= 2)
                                {
                                    string correction = FindWordCorrection(word, previousLiveWord, true);
                                    if (!string.IsNullOrEmpty(correction) && !correction.Equals(word, StringComparison.Ordinal))
                                    {
                                        uint sent = SendAtomicReplacement(word.Length, correction, false, c);
                                        if (sent > 0)
                                        {
                                            lastOriginalWord = word;
                                            lastCorrectedWord = correction;
                                            previousLiveWord = "";
                                            lastWasAutocorrected = true;
                                            return (IntPtr)1;
                                        }
                                    }
                                }
                                previousLiveWord = "";
                            }
                            else
                            {
                                currentWord.Clear();
                                previousLiveWord = "";
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("HookCallback exception: " + ex.Message);
            }
        }
        return CallNextHookEx(hookId, nCode, wParam, lParam);
    }

    private static readonly object logLock = new object();
    private static readonly Queue<string> logQueue = new Queue<string>();
    private static readonly AutoResetEvent logSignal = new AutoResetEvent(false);
    private static int logWorkerStarted = 0;
    private static volatile bool stopLogWorker = false;

    public static void Log(string msg)
    {
        try
        {
            string line = string.Format("[{0:HH:mm:ss}] {1}\r\n", DateTime.Now, msg);
            lock (logLock)
            {
                if (logQueue.Count < 1000) logQueue.Enqueue(line);
            }
            if (Interlocked.CompareExchange(ref logWorkerStarted, 1, 0) == 0)
            {
                Thread t = new Thread(LogWorkerLoop);
                t.IsBackground = true;
                t.Priority = ThreadPriority.BelowNormal;
                t.Start();
            }
            logSignal.Set();
        }
        catch { }
    }

    private static void LogWorkerLoop()
    {
        while (!stopLogWorker)
        {
            logSignal.WaitOne(350);
            FlushLogQueue();
        }
        FlushLogQueue();
    }

    private static void FlushLogQueue()
    {
        string batchText = null;
        lock (logLock)
        {
            if (logQueue.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            while (logQueue.Count > 0) sb.Append(logQueue.Dequeue());
            batchText = sb.ToString();
        }
        if (string.IsNullOrEmpty(batchText)) return;
        try
        {
            string p = Path.Combine(GetAppDir(), "log.txt");
            try
            {
                FileInfo fi = new FileInfo(p);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    string old = Path.Combine(GetAppDir(), "log.old.txt");
                    try { if (File.Exists(old)) File.Delete(old); } catch { }
                    File.Move(p, old);
                }
            }
            catch { }
            File.AppendAllText(p, batchText, Encoding.UTF8);
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        try
        {
            if (targetTrackTimer != null) { targetTrackTimer.Stop(); targetTrackTimer.Dispose(); targetTrackTimer = null; }
            if (watchdogTimer != null) { watchdogTimer.Stop(); watchdogTimer.Dispose(); watchdogTimer = null; }
            if (delayedRehookTimer != null) { delayedRehookTimer.Stop(); delayedRehookTimer.Dispose(); delayedRehookTimer = null; }
            stopPingThread = true;
            stopLogWorker = true;
            try { logSignal.Set(); } catch { }
            FlushLogQueue();
            if (hotReloadTimer != null)
            {
                hotReloadTimer.Stop();
                hotReloadTimer.Dispose();
                hotReloadTimer = null;
            }
            if (pingEvent != null)
            {
                try { pingEvent.Set(); pingEvent.Close(); } catch { }
                pingEvent = null;
            }
            if (grammarlyPill != null && !grammarlyPill.IsDisposed)
            {
                grammarlyPill.Close();
                grammarlyPill = null;
            }
            if (hotkeyWindow != null)
            {
                hotkeyWindow.Dispose();
                hotkeyWindow = null;
            }
            if (hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(hookId);
                hookId = IntPtr.Zero;
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
        }
        catch { }
        base.ExitThreadCore();
    }

    [STAThread]
    public static int Main()
    {
        bool createdNew = true;
        Mutex mutex = null;
        try
        {
            string desk = GetCurrentDesktopName();
            string safeDesk = Regex.Replace(desk, @"[^A-Za-z0-9_]", "_");
            string mutexName = "Spacjomat_Live_v12_" + safeDesk;
            string pingName = "Spacjomat_Ping_v12_" + safeDesk;

            try
            {
                mutex = new Mutex(true, mutexName, out createdNew);
            }
            catch (UnauthorizedAccessException)
            {
                createdNew = false;
            }

            if (!createdNew)
            {
                bool pinged = false;
                try
                {
                    using (var ev = EventWaitHandle.OpenExisting(pingName))
                    {
                        ev.Set();
                        pinged = true;
                    }
                }
                catch { }

                if (!pinged)
                {
                    MessageBox.Show("Spacjomat jest już włączony i czuwa w tle!\n\n• F7 lub Ctrl+Shift+L = TYLKO AI (całe pole)\n• F8 lub Ctrl+Shift+K = TYLKO szybka baza Offline (~2 ms)\n\nProblem ze skrótami? Kliknij ikonę Spacjomatu przy zegarku → 🩺 Diagnostyka.", "Spacjomat", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
                }
                return 99;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SpacjomatContext());
            return IntentionalExit ? 99 : 1;
        }
        catch (Exception ex)
        {
            Log("Fatal Main error: " + ex.ToString());
            FlushLogQueue();
            return 1;
        }
        finally
        {
            if (mutex != null && createdNew)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
        }
    }
}

public class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    private class HotkeyEntry
    {
        public int Id;
        public uint Mods;
        public uint Vk;
        public string Name;
        public bool IsAi;
        public bool Registered;
    }

    private readonly List<HotkeyEntry> entries = new List<HotkeyEntry>();

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());

        // Skroty OFFLINE (F8 + alternatywy, ktore dzialaja nawet przy wlaczonym Fn Lock)
        entries.Add(new HotkeyEntry { Id = 1, Mods = MOD_NOREPEAT | MOD_CONTROL | MOD_SHIFT, Vk = 0x4B, Name = "Ctrl+Shift+K", IsAi = false });
        entries.Add(new HotkeyEntry { Id = 2, Mods = MOD_NOREPEAT | MOD_CONTROL | MOD_ALT,   Vk = 0x4B, Name = "Ctrl+Alt+K",   IsAi = false });
        entries.Add(new HotkeyEntry { Id = 3, Mods = MOD_NOREPEAT,                           Vk = 0x77, Name = "F8",            IsAi = false });
        // Skroty AI (F7 + Ctrl+Shift+L). Ctrl+Alt+L swiadomie pominiete: to AltGr+L = litera "l" z kreska.
        entries.Add(new HotkeyEntry { Id = 4, Mods = MOD_NOREPEAT,                           Vk = 0x76, Name = "F7",            IsAi = true });
        entries.Add(new HotkeyEntry { Id = 5, Mods = MOD_NOREPEAT | MOD_CONTROL | MOD_SHIFT, Vk = 0x4C, Name = "Ctrl+Shift+L", IsAi = true });

        EnsureRegistered();
        SpacjomatContext.Log("Native Hotkeys: " + Summary());
    }

    // Probuje zarejestrowac ponownie skroty (z opcja twardego odswiezenia po wybudzeniu/odblokowaniu).
    public void EnsureRegistered(bool forceReRegister = false)
    {
        foreach (HotkeyEntry en in entries)
        {
            if (forceReRegister && en.Registered)
            {
                try { SpacjomatContext.UnregisterHotKey(this.Handle, en.Id); } catch { }
                en.Registered = false;
            }
            if (en.Registered) continue;
            en.Registered = SpacjomatContext.RegisterHotKey(this.Handle, en.Id, en.Mods, en.Vk);
        }
    }

    public int FailedCount()
    {
        int n = 0;
        foreach (HotkeyEntry en in entries) if (!en.Registered) n++;
        return n;
    }

    public string Summary()
    {
        List<string> parts = new List<string>();
        foreach (HotkeyEntry en in entries) parts.Add(en.Name + "=" + (en.Registered ? "OK" : "ZAJETY"));
        return string.Join(", ", parts.ToArray());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            bool ai = false;
            foreach (HotkeyEntry en in entries) if (en.Id == id) { ai = en.IsAi; break; }
            SpacjomatContext.Log("Native hotkey id=" + id + (ai ? " -> AI" : " -> Offline"));
            if (ai) SpacjomatContext.TriggerInPlaceCorrection(true, true, false);
            else SpacjomatContext.TriggerInPlaceCorrection(false, false, true);
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try
        {
            foreach (HotkeyEntry en in entries)
            {
                if (en.Registered) SpacjomatContext.UnregisterHotKey(this.Handle, en.Id);
                en.Registered = false;
            }
            DestroyHandle();
        }
        catch { }
    }
}

public class FloatingBanner : Form
{
    private Label lblText;
    private System.Windows.Forms.Timer hideTimer;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    public FloatingBanner()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.Size = new Size(580, 48);
        this.BackColor = Color.FromArgb(15, 23, 42);

        int screenWidth = Screen.PrimaryScreen.WorkingArea.Width;
        this.Location = new Point((screenWidth - this.Width) / 2, 35);

        lblText = new Label();
        lblText.Dock = DockStyle.Fill;
        lblText.TextAlign = ContentAlignment.MiddleCenter;
        lblText.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        lblText.ForeColor = Color.FromArgb(248, 250, 252);
        this.Controls.Add(lblText);

        hideTimer = new System.Windows.Forms.Timer();
        hideTimer.Interval = 3200;
        hideTimer.Tick += (s, e) => { hideTimer.Stop(); this.Hide(); };

        this.Paint += (s, e) => {
            using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1.5f))
            {
                e.Graphics.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        };

        IntPtr forceHwnd = this.Handle;
    }

    public void ShowMessage(string message)
    {
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(() => ShowMessage(message))); } catch { }
            return;
        }

        lblText.Text = message;
        try
        {
            // Zawsze na ekranie, na ktorym aktualnie pracujesz (dokowanie / drugi monitor / zmiana rozdzielczosci).
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            this.Location = new Point(wa.Left + Math.Max(0, (wa.Width - this.Width) / 2), wa.Top + 35);
        }
        catch { }
        SpacjomatContext.ShowTopmostNoActivate(this);
        hideTimer.Stop();
        hideTimer.Start();
    }
}

// -------------------------------------------------------------
// Grammarly-style Discreet Non-Activating Floating Pill
// -------------------------------------------------------------
public class GrammarlyPillForm : Form
{
    private SpacjomatContext context;
    private Button btnFix;
    private Button btnOffline;
    private Button btnStudio;
    private Button btnCfg;
    private Label lblDot;
    private Label lblResize;
    private ToolTip tip;

    private bool dragging = false;
    private bool dragMoved = false;
    private Point dragMouseStart;
    private Point dragFormStart;

    private bool resizing = false;
    private Point resizeStartScreen;
    private Size resizeStartSize;
    private int lastFontH = -1;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE (never steals focus!)
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW (never on taskbar!)
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    public GrammarlyPillForm(SpacjomatContext ctx)
    {
        this.context = ctx;
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.DoubleBuffered = true;
        this.Size = new Size(265, 44);
        this.BackColor = Color.FromArgb(15, 23, 42);
        this.Opacity = 0.95;

        tip = new ToolTip();

        lblDot = new Label();
        lblDot.Text = "●";
        lblDot.TextAlign = ContentAlignment.MiddleCenter;
        lblDot.ForeColor = Color.FromArgb(34, 197, 94);
        lblDot.BackColor = Color.Transparent;
        lblDot.Cursor = Cursors.SizeAll;
        lblDot.MouseDown += StartDrag;
        lblDot.MouseMove += DoDrag;
        lblDot.MouseUp += EndDrag;
        tip.SetToolTip(lblDot, "Złap i przeciągnij, aby przesunąć pigułkę");
        this.Controls.Add(lblDot);

        btnFix = new Button();
        btnFix.Text = "🧠 AI (F7)";
        btnFix.FlatStyle = FlatStyle.Flat;
        btnFix.FlatAppearance.BorderSize = 0;
        btnFix.FlatAppearance.MouseOverBackColor = Color.FromArgb(51, 65, 85);
        btnFix.BackColor = Color.FromArgb(30, 41, 59);
        btnFix.ForeColor = Color.FromArgb(248, 250, 252);
        btnFix.Cursor = Cursors.Hand;
        btnFix.TabStop = false;
        btnFix.Click += (s, e) => SpacjomatContext.TriggerInPlaceCorrection(true, true, false);
        this.Controls.Add(btnFix);

        btnOffline = new Button();
        btnOffline.Text = "⚡ F8";
        btnOffline.FlatStyle = FlatStyle.Flat;
        btnOffline.FlatAppearance.BorderSize = 0;
        btnOffline.FlatAppearance.MouseOverBackColor = Color.FromArgb(17, 94, 89);
        btnOffline.BackColor = Color.FromArgb(19, 78, 74);
        btnOffline.ForeColor = Color.FromArgb(204, 251, 241);
        btnOffline.Cursor = Cursors.Hand;
        btnOffline.TabStop = false;
        btnOffline.Click += (s, e) => SpacjomatContext.TriggerInPlaceCorrection(false, false, true);
        tip.SetToolTip(btnOffline, "F8 = Błyskawiczna korekta Bazą Offline (4.5M SJP + GBoard + 29K reguł, 0–5 ms)\nPoprawia zaznaczenie albo całe pole bez AI!");
        this.Controls.Add(btnOffline);

        btnStudio = new Button();
        btnStudio.Text = "📝";
        btnStudio.FlatStyle = FlatStyle.Flat;
        btnStudio.FlatAppearance.BorderSize = 0;
        btnStudio.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 41, 59);
        btnStudio.BackColor = Color.FromArgb(15, 23, 42);
        btnStudio.ForeColor = Color.FromArgb(186, 230, 253);
        btnStudio.Cursor = Cursors.Hand;
        btnStudio.TabStop = false;
        btnStudio.Click += (s, e) => { if (context != null) context.OpenStudio(); };
        tip.SetToolTip(btnStudio, "Otwórz Edytor Studio");
        this.Controls.Add(btnStudio);

        btnCfg = new Button();
        btnCfg.Text = "⚙";
        btnCfg.FlatStyle = FlatStyle.Flat;
        btnCfg.FlatAppearance.BorderSize = 0;
        btnCfg.FlatAppearance.MouseOverBackColor = Color.FromArgb(30, 41, 59);
        btnCfg.BackColor = Color.FromArgb(15, 23, 42);
        btnCfg.ForeColor = Color.FromArgb(148, 163, 184);
        btnCfg.Cursor = Cursors.Hand;
        btnCfg.TabStop = false;
        btnCfg.Click += (s, e) => { if (context != null) context.OpenAiSettings(); };
        tip.SetToolTip(btnCfg, "Ustawienia AI, limitów i rozmiaru pigułki");
        this.Controls.Add(btnCfg);

        lblResize = new Label();
        lblResize.Text = "◢";
        lblResize.TextAlign = ContentAlignment.BottomRight;
        lblResize.BackColor = Color.Transparent;
        lblResize.ForeColor = Color.FromArgb(100, 116, 139);
        lblResize.Font = new Font("Segoe UI", 8F, FontStyle.Regular);
        lblResize.Cursor = Cursors.SizeNWSE;
        lblResize.MouseDown += StartResize;
        lblResize.MouseMove += DoResize;
        lblResize.MouseUp += EndResize;
        tip.SetToolTip(lblResize, "Przeciągnij, aby zmienić wymiary pigułki");
        this.Controls.Add(lblResize);

        this.Paint += OnPillPaint;
        this.MouseDown += StartDrag;
        this.MouseMove += DoDrag;
        this.MouseUp += EndDrag;

        ApplyConfig(SpacjomatContext.aiConfig);
        IntPtr forceHwnd = this.Handle;
    }

    private static GraphicsPath CreateRoundRect(Rectangle r, int radius)
    {
        GraphicsPath gp = new GraphicsPath();
        if (radius <= 1)
        {
            gp.AddRectangle(r);
            return gp;
        }
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 2) d = 2;
        gp.AddArc(r.X, r.Y, d, d, 180, 90);
        gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        gp.CloseFigure();
        return gp;
    }

    private void OnPillPaint(object s, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int rad = Math.Min(18, Math.Max(8, this.Height / 2 - 2));
        Rectangle borderRect = new Rectangle(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath gp = CreateRoundRect(borderRect, rad))
        using (LinearGradientBrush bg = new LinearGradientBrush(borderRect, Color.FromArgb(20, 30, 52), Color.FromArgb(12, 18, 34), 90f))
        using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1.4f))
        {
            e.Graphics.FillPath(bg, gp);
            e.Graphics.DrawPath(p, gp);
        }
    }

    private void UpdateRoundedRegion()
    {
        try
        {
            int rad = Math.Min(18, Math.Max(8, this.Height / 2 - 2));
            using (GraphicsPath gp = CreateRoundRect(new Rectangle(0, 0, this.Width, this.Height), rad))
            {
                Region old = this.Region;
                this.Region = new Region(gp);
                if (old != null) old.Dispose();
            }
            this.Invalidate();
        }
        catch { }
    }

    private void Relayout()
    {
        int w = this.Width;
        int h = this.Height;
        int pad = Math.Max(4, Math.Min(10, h / 6));
        int innerH = Math.Max(20, h - pad * 2);

        if (lastFontH < 0 || Math.Abs(h - lastFontH) >= 2)
        {
            lastFontH = h;
            float basePt = Math.Max(7.6F, Math.Min(13.5F, h * 0.22F));
            lblDot.Font = new Font("Segoe UI", Math.Max(8.5F, basePt + 1.0F), FontStyle.Bold);
            btnFix.Font = new Font("Segoe UI", basePt, FontStyle.Bold);
            btnOffline.Font = new Font("Segoe UI", Math.Max(7.5F, basePt - 0.3F), FontStyle.Bold);
            btnStudio.Font = new Font("Segoe UI", basePt, FontStyle.Regular);
            btnCfg.Font = new Font("Segoe UI", basePt + 0.5F, FontStyle.Regular);
        }

        int dotW = Math.Max(15, Math.Min(26, (int)(h * 0.44)));
        int gripW = 12;
        int cfgW = Math.Max(24, Math.Min(38, innerH));
        bool showStudio = (w >= 295);
        int studioW = 0;
        if (showStudio)
        {
            studioW = (w >= 430) ? Math.Max(76, (int)(h * 1.65)) : Math.Max(26, Math.Min(38, innerH));
            btnStudio.Text = (w >= 430) ? "📝 Studio" : "📝";
        }
        btnStudio.Visible = showStudio;

        int offlineW = (w >= 340) ? Math.Max(72, (int)(h * 1.65)) : ((w >= 230) ? 54 : 42);
        btnOffline.Text = (w >= 340) ? "⚡ Baza F8" : "⚡ F8";

        int rightCursor = w - 3 - gripW;
        lblResize.Location = new Point(rightCursor, h - 16);
        lblResize.Size = new Size(gripW, 14);

        rightCursor -= cfgW + 2;
        btnCfg.Location = new Point(rightCursor, pad);
        btnCfg.Size = new Size(cfgW, innerH);

        if (showStudio)
        {
            rightCursor -= studioW + 2;
            btnStudio.Location = new Point(rightCursor, pad);
            btnStudio.Size = new Size(studioW, innerH);
        }

        rightCursor -= offlineW + 3;
        btnOffline.Location = new Point(rightCursor, pad);
        btnOffline.Size = new Size(offlineW, innerH);

        lblDot.Location = new Point(4, pad);
        lblDot.Size = new Size(dotW, innerH);

        int fixLeft = lblDot.Right + 2;
        int fixW = Math.Max(48, rightCursor - fixLeft - 3);
        btnFix.Location = new Point(fixLeft, pad);
        btnFix.Size = new Size(fixW, innerH);

        UpdateFixButtonText();
    }

    private void UpdateFixButtonText()
    {
        bool aiReady = AiCorrectionEngine.IsAiReady(SpacjomatContext.aiConfig);
        bool suggestMode = (SpacjomatContext.aiConfig == null || SpacjomatContext.aiConfig.CorrectionMode != "auto");
        if (this.Width < 215)
            btnFix.Text = aiReady ? "🧠 F7" : "🧠 AI!";
        else if (this.Width >= 335)
            btnFix.Text = aiReady ? "🧠 AI Cały tekst (F7)" : "🧠 Włącz AI (F7)";
        else
            btnFix.Text = aiReady ? "🧠 AI (F7)" : "🧠 Brak AI";

        int rpm, tpm;
        AiUsageData u = AiUsageTracker.GetSnapshot(out rpm, out tpm);
        string m = (SpacjomatContext.aiConfig != null && !string.IsNullOrEmpty(SpacjomatContext.aiConfig.Model))
            ? SpacjomatContext.aiConfig.Model : "-";
        string tipText = aiReady
            ? string.Format("F7 = TYLKO AI (pełna korekta całego tekstu)\nModel: {0} | Tryb: {1}\nDzisiaj: {2} zapytań (RPM: {3})",
                m, suggestMode ? "Podpowiedzi Grammarly" : "Szybka auto-zamiana", u.RequestsToday, rpm)
            : "F7 wymaga aktywnego AI — kliknij ⚙ i wklej klucz!\nDo szybkiej korekty bez AI użyj przycisku ⚡ F8.";
        tip.SetToolTip(btnFix, tipText);
    }

    public void ApplyConfig(AiConfig cfg)
    {
        if (cfg == null) return;
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action<AiConfig>(ApplyConfig), cfg); } catch { }
            return;
        }
        cfg.Clamp();
        if (this.Width != cfg.PillWidth || this.Height != cfg.PillHeight)
        {
            this.Size = new Size(cfg.PillWidth, cfg.PillHeight);
        }
        this.Opacity = Math.Max(0.45, Math.Min(1.0, cfg.PillOpacity / 100.0));

        if (cfg.PillX >= 0 && cfg.PillY >= 0)
        {
            Point pt = new Point(cfg.PillX, cfg.PillY);
            Rectangle wa = Screen.FromPoint(pt).WorkingArea;
            int cx = Math.Max(wa.Left, Math.Min(wa.Right - this.Width, pt.X));
            int cy = Math.Max(wa.Top, Math.Min(wa.Bottom - this.Height, pt.Y));
            this.Location = new Point(cx, cy);
        }
        else
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            this.Location = new Point(wa.Right - this.Width - 22, wa.Bottom - this.Height - 16);
        }

        Relayout();
        UpdateRoundedRegion();
        RefreshStatus();
    }

    private void StartDrag(object s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            dragMoved = false;
            dragMouseStart = Control.MousePosition;
            dragFormStart = this.Location;
        }
    }

    private void DoDrag(object s, MouseEventArgs e)
    {
        if (dragging)
        {
            Point cur = Control.MousePosition;
            int dx = cur.X - dragMouseStart.X;
            int dy = cur.Y - dragMouseStart.Y;
            if (Math.Abs(dx) > 2 || Math.Abs(dy) > 2) dragMoved = true;
            this.Location = new Point(dragFormStart.X + dx, dragFormStart.Y + dy);
        }
    }

    private void EndDrag(object s, MouseEventArgs e)
    {
        if (dragging && dragMoved && SpacjomatContext.aiConfig != null)
        {
            SpacjomatContext.aiConfig.PillX = this.Location.X;
            SpacjomatContext.aiConfig.PillY = this.Location.Y;
            SpacjomatContext.aiConfig.Save();
        }
        dragging = false;
    }

    private void StartResize(object s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            resizing = true;
            resizeStartScreen = Control.MousePosition;
            resizeStartSize = this.Size;
        }
    }

    private void DoResize(object s, MouseEventArgs e)
    {
        if (resizing)
        {
            Point cur = Control.MousePosition;
            int nw = Math.Max(160, Math.Min(620, resizeStartSize.Width + (cur.X - resizeStartScreen.X)));
            int nh = Math.Max(28, Math.Min(110, resizeStartSize.Height + (cur.Y - resizeStartScreen.Y)));
            if (nw != this.Width || nh != this.Height)
            {
                this.Size = new Size(nw, nh);
                Relayout();
                UpdateRoundedRegion();
            }
        }
    }

    private void EndResize(object s, MouseEventArgs e)
    {
        if (resizing && SpacjomatContext.aiConfig != null)
        {
            SpacjomatContext.aiConfig.PillWidth = this.Width;
            SpacjomatContext.aiConfig.PillHeight = this.Height;
            SpacjomatContext.aiConfig.PillSizePreset = "custom";
            SpacjomatContext.aiConfig.Clamp();
            SpacjomatContext.aiConfig.Save();
        }
        resizing = false;
    }

    public void RefreshStatus()
    {
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(RefreshStatus)); } catch { }
            return;
        }
        lblDot.ForeColor = SpacjomatContext.isEnabled ? Color.FromArgb(34, 197, 94) : Color.FromArgb(245, 158, 11);
        UpdateFixButtonText();
    }
}

// -------------------------------------------------------------
// Grammarly-style Non-Activating Suggestion Popup Card
// -------------------------------------------------------------
public class GrammarlySuggestionForm : Form
{
    private Panel pnlContent;
    private Label lblHeaderTitle;
    private bool dragging = false;
    private Point dragMouseStart;
    private Point dragFormStart;

    private IntPtr savedTargetHwnd = IntPtr.Zero;
    private bool savedSelectAll = false;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    public GrammarlySuggestionForm()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.StartPosition = FormStartPosition.Manual;
        this.DoubleBuffered = true;
        this.Size = new Size(470, 260);
        this.BackColor = Color.FromArgb(15, 23, 42);

        Panel header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(20, 30, 52),
            Padding = new Padding(12, 0, 8, 0),
            Cursor = Cursors.SizeAll
        };
        header.MouseDown += StartDrag;
        header.MouseMove += DoDrag;
        header.MouseUp += EndDrag;
        this.Controls.Add(header);

        lblHeaderTitle = new Label
        {
            Text = "🧠 Podpowiedzi Grammarly",
            ForeColor = Color.FromArgb(248, 250, 252),
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Cursor = Cursors.SizeAll
        };
        lblHeaderTitle.MouseDown += StartDrag;
        lblHeaderTitle.MouseMove += DoDrag;
        lblHeaderTitle.MouseUp += EndDrag;
        header.Controls.Add(lblHeaderTitle);

        Button btnClose = new Button
        {
            Text = "✖",
            Dock = DockStyle.Right,
            Width = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(20, 30, 52),
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btnClose.FlatAppearance.BorderSize = 0;
        btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);
        btnClose.Click += (s, e) => this.Hide();
        header.Controls.Add(btnClose);

        pnlContent = new Panel
        {
            Location = new Point(10, 42),
            Width = 450,
            BackColor = Color.FromArgb(15, 23, 42)
        };
        this.Controls.Add(pnlContent);

        this.Paint += (s, e) =>
        {
            using (Pen p = new Pen(Color.FromArgb(56, 189, 248), 1.5f))
            {
                e.Graphics.DrawRectangle(p, 0, 0, this.Width - 1, this.Height - 1);
            }
        };

        IntPtr forceHwnd = this.Handle;
    }

    private void StartDrag(object s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            dragging = true;
            dragMouseStart = Control.MousePosition;
            dragFormStart = this.Location;
        }
    }

    private void DoDrag(object s, MouseEventArgs e)
    {
        if (dragging)
        {
            Point cur = Control.MousePosition;
            this.Location = new Point(dragFormStart.X + (cur.X - dragMouseStart.X), dragFormStart.Y + (cur.Y - dragMouseStart.Y));
        }
    }

    private void EndDrag(object s, MouseEventArgs e) { dragging = false; }

    public void ShowSuggestions(string originalText, AiSuggestionResult result, long ms, IntPtr targetHwnd, bool selectAllFirst)
    {
        if (result == null || result.Options == null || result.Options.Count == 0) return;
        if (this.InvokeRequired)
        {
            try
            {
                this.BeginInvoke(new Action(() => ShowSuggestions(originalText, result, ms, targetHwnd, selectAllFirst)));
            }
            catch { }
            return;
        }

        savedTargetHwnd = targetHwnd;
        savedSelectAll = selectAllFirst;

        string modelTag = string.IsNullOrEmpty(result.ModelUsed) ? "AI" : result.ModelUsed;
        lblHeaderTitle.Text = result.IsOffline
            ? "⚡ Podpowiedź Offline (" + modelTag + ")"
            : string.Format("🧠 Podpowiedzi Grammarly • {0} ({1} ms)", modelTag, ms);

        foreach (Control c in pnlContent.Controls) c.Dispose();
        pnlContent.Controls.Clear();

        int cardW = 470;
        int innerW = cardW - 22;
        pnlContent.Width = innerW;
        int y = 2;

        if (result.Fixes != null && result.Fixes.Count > 0)
        {
            string fixesText = "Poprawki: " + string.Join("   •   ", result.Fixes.ToArray());
            Size fSz = TextRenderer.MeasureText(fixesText, new Font("Segoe UI", 8.5F, FontStyle.Bold), new Size(innerW - 12, 999), TextFormatFlags.WordBreak);
            Label lblFixes = new Label
            {
                Text = fixesText,
                Left = 2,
                Top = y,
                Width = innerW - 4,
                Height = Math.Max(22, fSz.Height + 6),
                BackColor = result.IsOffline ? Color.FromArgb(69, 26, 3) : Color.FromArgb(6, 78, 59),
                ForeColor = result.IsOffline ? Color.FromArgb(253, 230, 138) : Color.FromArgb(167, 243, 208),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Padding = new Padding(6, 4, 6, 4)
            };
            pnlContent.Controls.Add(lblFixes);
            y += lblFixes.Height + 6;
        }

        string previewOrig = originalText.Replace("\r", " ").Replace("\n", " ");
        if (previewOrig.Length > 110) previewOrig = previewOrig.Substring(0, 107) + "...";
        Label lblOrig = new Label
        {
            Text = "Było: " + previewOrig,
            Left = 2,
            Top = y,
            Width = innerW - 4,
            Height = 20,
            ForeColor = Color.FromArgb(148, 163, 184),
            Font = new Font("Segoe UI", 8.2F, FontStyle.Italic)
        };
        pnlContent.Controls.Add(lblOrig);
        y += 24;

        Font optTitleFont = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        Font optBodyFont = new Font("Segoe UI", 9.8F, FontStyle.Regular);

        int optCount = Math.Min(3, result.Options.Count);
        for (int i = 0; i < optCount; i++)
        {
            AiSuggestionOption opt = result.Options[i];
            string chosenText = opt.Text;

            Size textSz = TextRenderer.MeasureText(chosenText, optBodyFont, new Size(innerW - 24, 999), TextFormatFlags.WordBreak);
            int textH = Math.Max(24, Math.Min(120, textSz.Height + 6));
            int boxH = 26 + textH + 38;

            Panel card = new Panel
            {
                Left = 2,
                Top = y,
                Width = innerW - 4,
                Height = boxH,
                BackColor = (i == 0) ? Color.FromArgb(30, 41, 59) : Color.FromArgb(23, 33, 50)
            };

            Label lblKind = new Label
            {
                Text = (i + 1) + ". " + (opt.Label ?? "Propozycja"),
                Left = 10,
                Top = 6,
                Width = card.Width - 20,
                Height = 18,
                ForeColor = (i == 0) ? Color.FromArgb(56, 189, 248) : Color.FromArgb(196, 181, 253),
                Font = optTitleFont
            };
            card.Controls.Add(lblKind);

            Label lblBody = new Label
            {
                Text = chosenText,
                Left = 10,
                Top = 26,
                Width = card.Width - 20,
                Height = textH,
                ForeColor = Color.FromArgb(248, 250, 252),
                Font = optBodyFont
            };
            card.Controls.Add(lblBody);

            int btnY = 28 + textH;
            Button btnInsert = new Button
            {
                Text = (i == 0) ? "✔ Wstaw tę wersję" : "✔ Wybierz",
                Left = 10,
                Top = btnY,
                Width = (i == 0) ? 160 : 125,
                Height = 28,
                BackColor = (i == 0) ? Color.FromArgb(37, 99, 235) : Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.8F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnInsert.FlatAppearance.BorderSize = 0;
            btnInsert.Click += (s, e) =>
            {
                this.Hide();
                SpacjomatContext.ApplySuggestedText(chosenText, savedTargetHwnd, savedSelectAll);
            };
            card.Controls.Add(btnInsert);

            Button btnCopyOpt = new Button
            {
                Text = "📋 Kopiuj",
                Left = btnInsert.Right + 8,
                Top = btnY,
                Width = 92,
                Height = 28,
                BackColor = Color.FromArgb(15, 23, 42),
                ForeColor = Color.FromArgb(203, 213, 225),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            btnCopyOpt.FlatAppearance.BorderSize = 0;
            btnCopyOpt.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetDataObject(chosenText, true, 4, 30);
                    btnCopyOpt.Text = "✔ Skopiowano";
                }
                catch { }
            };
            card.Controls.Add(btnCopyOpt);

            pnlContent.Controls.Add(card);
            y += boxH + 8;
        }

        Button btnDismiss = new Button
        {
            Text = "✖ Pomiń",
            Left = innerW - 96,
            Top = y,
            Width = 92,
            Height = 26,
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(148, 163, 184),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F),
            Cursor = Cursors.Hand,
            TabStop = false
        };
        btnDismiss.FlatAppearance.BorderSize = 0;
        btnDismiss.Click += (s, e) => this.Hide();
        pnlContent.Controls.Add(btnDismiss);

        Label lblHint = new Label
        {
            Text = "Możesz też wyłączyć podpowiedzi i wrócić do auto-zamiany w ⚙",
            Left = 2,
            Top = y + 4,
            Width = innerW - 104,
            Height = 20,
            ForeColor = Color.FromArgb(100, 116, 139),
            Font = new Font("Segoe UI", 7.8F)
        };
        pnlContent.Controls.Add(lblHint);
        y += 34;

        pnlContent.Height = y;
        this.Size = new Size(cardW, 42 + y + 8);

        Point anchorPt = (SpacjomatContext.grammarlyPill != null && SpacjomatContext.grammarlyPill.Visible)
            ? new Point(SpacjomatContext.grammarlyPill.Right - this.Width, SpacjomatContext.grammarlyPill.Top - this.Height - 10)
            : new Point(Cursor.Position.X - this.Width / 2, Cursor.Position.Y - this.Height - 24);
        Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        int fx = Math.Max(wa.Left + 8, Math.Min(wa.Right - this.Width - 8, anchorPt.X));
        int fy = Math.Max(wa.Top + 8, Math.Min(wa.Bottom - this.Height - 8, anchorPt.Y));
        this.Location = new Point(fx, fy);

        SpacjomatContext.ShowTopmostNoActivate(this);
        this.Invalidate();
    }
}

// -------------------------------------------------------------
// AI & Grammarly Settings Form
// -------------------------------------------------------------
public class AiSettingsForm : Form
{
    private SpacjomatContext context;
    private CheckBox chkEnableAi;
    private CheckBox chkShowPill;
    private CheckBox chkFastHedge;
    private ComboBox cmbCorrectionMode;
    private ComboBox cmbProvider;
    private TextBox txtApiKey;
    private Button btnToggleKey;
    private LinkLabel lnkKeyPortal;
    private ComboBox cmbModel;
    private Button btnDetectModels;
    private ComboBox cmbStyle;
    private TextBox txtEndpoint;

    // Quota & Usage HUD controls
    private Panel pnlBarBg;
    private Panel pnlBarFill;
    private Label lblDailyQuota;
    private Label lblRpmAndTokens;
    private Label lblModelAndRemote;
    private NumericUpDown numDailyLimit;
    private NumericUpDown numRpmLimit;

    // Pill Appearance controls
    private ComboBox cmbPillPreset;
    private NumericUpDown numPillW;
    private NumericUpDown numPillH;
    private NumericUpDown numPillOpacity;

    private TextBox txtTestStatus;
    private Button btnTest;

    private bool suppressUiEvents = false;
    private bool saved = false;
    private int tempPillX = -1;
    private int tempPillY = -1;

    public AiSettingsForm(Icon icon, SpacjomatContext ctx)
    {
        this.context = ctx;
        this.Text = "Ustawienia AI, Limitów Klucza i Grammarly — Spacjomat";
        this.Icon = icon;
        this.AutoScaleMode = AutoScaleMode.Font;
        this.ClientSize = new Size(760, 745);
        this.MinimumSize = new Size(660, 560);
        this.FormBorderStyle = FormBorderStyle.Sizable;
        this.MaximizeBox = false;
        this.AutoScroll = true;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(248, 250, 252);
        this.Font = new Font("Segoe UI", 9.5F, FontStyle.Regular);

        int maxH = Screen.PrimaryScreen.WorkingArea.Height - 40;
        if (maxH > 580 && this.Height > maxH)
        {
            this.Height = maxH;
        }

        int y = 12;
        chkEnableAi = new CheckBox
        {
            Text = "Włącz korektę AI (Gemini / Groq / OpenAI) dla całych zdań (F7, F8, Ctrl+Shift+K, Studio)",
            Left = 20,
            Top = y,
            Width = 710,
            Height = 25,
            AutoSize = false
        };
        this.Controls.Add(chkEnableAi);
        y += 28;

        chkShowPill = new CheckBox
        {
            Text = "Pokaż pływającą pigułkę Grammarly na ekranie",
            Left = 20,
            Top = y,
            Width = 340,
            Height = 25,
            AutoSize = false
        };
        chkShowPill.CheckedChanged += (s, e) => { if (!suppressUiEvents) PreviewPillNow(); };
        this.Controls.Add(chkShowPill);

        chkFastHedge = new CheckBox
        {
            Text = "⚡ Tryb Turbo F7 (asekuracja przy zwisie >1.9 s)",
            Left = 370,
            Top = y,
            Width = 360,
            Height = 25,
            AutoSize = false
        };
        this.Controls.Add(chkFastHedge);
        y += 32;

        this.Controls.Add(new Label { Text = "Tryb pracy F7 / F8:", Left = 20, Top = y + 3, Width = 175, Height = 24 });
        cmbCorrectionMode = new ComboBox { Left = 200, Top = y, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbCorrectionMode.Items.AddRange(new object[]
        {
            "🎯 Karta podpowiedzi Grammarly (pokazuje błędy i 2–3 warianty do wyboru)",
            "⚡ Natychmiastowa auto-podmiana (od razu wkleja 1 najlepszą poprawkę)"
        });
        this.Controls.Add(cmbCorrectionMode);
        y += 34;

        this.Controls.Add(new Label { Text = "Dostawca AI:", Left = 20, Top = y + 3, Width = 175, Height = 24 });
        cmbProvider = new ComboBox { Left = 200, Top = y, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbProvider.Items.AddRange(new object[]
        {
            "gemini — Google Gemini API (darmowy klucz z Google AI Studio)",
            "groq — Groq API (darmowy, ~200 ms + dokładne limity z API)",
            "openai — OpenAI / OpenRouter / inne kompatybilne API",
            "ollama — Lokalna Ollama (offline, bez klucza i bez limitów)"
        });
        cmbProvider.SelectedIndexChanged += (s, e) => OnProviderPresetChanged();
        this.Controls.Add(cmbProvider);
        y += 34;

        this.Controls.Add(new Label { Text = "Klucz API:", Left = 20, Top = y + 3, Width = 175, Height = 24 });
        txtApiKey = new TextBox { Left = 200, Top = y, Width = 476, UseSystemPasswordChar = true };
        this.Controls.Add(txtApiKey);

        btnToggleKey = new Button
        {
            Text = "👁",
            Left = 682,
            Top = y - 1,
            Width = 38,
            Height = 26,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(226, 232, 240)
        };
        btnToggleKey.Click += (s, e) => { txtApiKey.UseSystemPasswordChar = !txtApiKey.UseSystemPasswordChar; };
        this.Controls.Add(btnToggleKey);
        y += 27;

        lnkKeyPortal = new LinkLabel
        {
            Text = "Darmowy klucz: aistudio.google.com/apikey  |  Alternatywa Groq: console.groq.com/keys",
            Left = 200,
            Top = y,
            Width = 520,
            Height = 20,
            Font = new Font("Segoe UI", 8.2F),
            LinkColor = Color.FromArgb(37, 99, 235)
        };
        lnkKeyPortal.LinkArea = new LinkArea(15, 26);
        lnkKeyPortal.LinkClicked += (s, e) =>
        {
            string url = (cmbProvider.SelectedIndex == 1) ? "https://console.groq.com/keys" : "https://aistudio.google.com/apikey";
            try { System.Diagnostics.Process.Start(url); } catch { }
        };
        this.Controls.Add(lnkKeyPortal);
        y += 24;

        this.Controls.Add(new Label { Text = "Model AI:", Left = 20, Top = y + 3, Width = 175, Height = 24 });
        cmbModel = new ComboBox { Left = 200, Top = y, Width = 365, DropDownStyle = ComboBoxStyle.DropDown };
        cmbModel.Items.AddRange(new object[]
        {
            "gemini-3.1-flash-lite",
            "gemini-3.5-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.8-flash",
            "llama-3.3-70b-versatile",
            "gpt-4o-mini"
        });
        this.Controls.Add(cmbModel);

        btnDetectModels = new Button
        {
            Text = "🔍 Wykryj modele",
            Left = 572,
            Top = y - 1,
            Width = 148,
            Height = 28,
            BackColor = Color.FromArgb(224, 242, 254),
            ForeColor = Color.FromArgb(3, 105, 161),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.8F, FontStyle.Bold)
        };
        btnDetectModels.Click += (s, e) => DetectModels();
        this.Controls.Add(btnDetectModels);
        y += 34;

        this.Controls.Add(new Label { Text = "Styl korekty:", Left = 20, Top = y + 3, Width = 175, Height = 24 });
        cmbStyle = new ComboBox { Left = 200, Top = y, Width = 520, DropDownStyle = ComboBoxStyle.DropDownList };
        cmbStyle.Items.AddRange(new object[]
        {
            "natural (Naturalny czat — zachowaj mój ton i slang)",
            "business (Biznesowy / formalny)",
            "strict (Minimalna korekta — tylko błędy i sens)"
        });
        this.Controls.Add(cmbStyle);
        y += 34;

        this.Controls.Add(new Label { Text = "Własny URL (opcjonalnie):", Left = 20, Top = y + 3, Width = 178, Height = 24 });
        txtEndpoint = new TextBox { Left = 200, Top = y, Width = 520 };
        this.Controls.Add(txtEndpoint);
        y += 34;

        // ---------------- QUOTA & USAGE SECTION ----------------
        GroupBox grpQuota = new GroupBox
        {
            Text = "📊 Statystyki użycia i limity Twojego klucza AI (ile zostało)",
            Left = 20,
            Top = y,
            Width = 700,
            Height = 162,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 23, 42)
        };
        this.Controls.Add(grpQuota);

        Font fReg = new Font("Segoe UI", 8.8F, FontStyle.Regular);
        Font fBold = new Font("Segoe UI", 8.8F, FontStyle.Bold);

        pnlBarBg = new Panel
        {
            Left = 14,
            Top = 27,
            Width = 280,
            Height = 18,
            BackColor = Color.FromArgb(226, 232, 240)
        };
        grpQuota.Controls.Add(pnlBarBg);

        pnlBarFill = new Panel
        {
            Left = 0,
            Top = 0,
            Width = 0,
            Height = 18,
            BackColor = Color.FromArgb(22, 163, 74)
        };
        pnlBarBg.Controls.Add(pnlBarFill);

        lblDailyQuota = new Label
        {
            Left = 304,
            Top = 26,
            Width = 384,
            Height = 20,
            Font = fBold,
            ForeColor = Color.FromArgb(15, 23, 42)
        };
        grpQuota.Controls.Add(lblDailyQuota);

        lblRpmAndTokens = new Label
        {
            Left = 14,
            Top = 53,
            Width = 674,
            Height = 20,
            Font = fReg,
            ForeColor = Color.FromArgb(51, 65, 85)
        };
        grpQuota.Controls.Add(lblRpmAndTokens);

        lblModelAndRemote = new Label
        {
            Left = 14,
            Top = 76,
            Width = 674,
            Height = 36,
            Font = new Font("Segoe UI", 8.1F, FontStyle.Regular),
            ForeColor = Color.FromArgb(71, 85, 105)
        };
        grpQuota.Controls.Add(lblModelAndRemote);

        grpQuota.Controls.Add(new Label { Text = "Limit/dzień:", Left = 14, Top = 123, Width = 78, Height = 20, Font = fReg });
        numDailyLimit = new NumericUpDown { Left = 94, Top = 120, Width = 68, Minimum = 20, Maximum = 100000, Increment = 50, Font = fReg };
        numDailyLimit.ValueChanged += (s, e) => { if (!suppressUiEvents) RefreshUsageView(); };
        grpQuota.Controls.Add(numDailyLimit);

        grpQuota.Controls.Add(new Label { Text = "Limit/min:", Left = 172, Top = 123, Width = 68, Height = 20, Font = fReg });
        numRpmLimit = new NumericUpDown { Left = 242, Top = 120, Width = 58, Minimum = 1, Maximum = 5000, Increment = 5, Font = fReg };
        numRpmLimit.ValueChanged += (s, e) => { if (!suppressUiEvents) RefreshUsageView(); };
        grpQuota.Controls.Add(numRpmLimit);

        Button btnOpenQuotaDash = new Button
        {
            Text = "📊 Oficjalne limity AI Studio",
            Left = 324,
            Top = 118,
            Width = 190,
            Height = 28,
            BackColor = Color.FromArgb(224, 242, 254),
            ForeColor = Color.FromArgb(3, 105, 161),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
        };
        btnOpenQuotaDash.Click += (s, e) =>
        {
            string url = (cmbProvider.SelectedIndex == 1) ? "https://console.groq.com/settings/limits" : "https://aistudio.google.com/rate-limit";
            try { System.Diagnostics.Process.Start(url); } catch { }
        };
        grpQuota.Controls.Add(btnOpenQuotaDash);

        Button btnResetUsage = new Button
        {
            Text = "🔄 Zeruj",
            Left = 522,
            Top = 118,
            Width = 80,
            Height = 28,
            BackColor = Color.FromArgb(241, 245, 249),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F)
        };
        btnResetUsage.Click += (s, e) => { AiUsageTracker.Reset(); RefreshUsageView(); };
        grpQuota.Controls.Add(btnResetUsage);

        Button btnRefreshUsage = new Button
        {
            Text = "↻ Odśwież",
            Left = 608,
            Top = 118,
            Width = 80,
            Height = 28,
            BackColor = Color.FromArgb(241, 245, 249),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 8.5F)
        };
        btnRefreshUsage.Click += (s, e) => RefreshUsageView();
        grpQuota.Controls.Add(btnRefreshUsage);

        y += 172;

        // ---------------- PILL APPEARANCE SECTION ----------------
        GroupBox grpPill = new GroupBox
        {
            Text = "🎨 Wygląd i wymiary pływającego ekraniku (pigułki Grammarly)",
            Left = 20,
            Top = y,
            Width = 700,
            Height = 122,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 41, 59)
        };
        this.Controls.Add(grpPill);

        grpPill.Controls.Add(new Label { Text = "Rozmiar:", Left = 14, Top = 28, Width = 72, Height = 22, Font = fReg });
        cmbPillPreset = new ComboBox { Left = 90, Top = 25, Width = 248, DropDownStyle = ComboBoxStyle.DropDownList, Font = fReg };
        cmbPillPreset.Items.AddRange(new object[]
        {
            "Kompaktowa (200 × 34 px)",
            "Średnia (265 × 44 px — zalecana)",
            "Duża (340 × 54 px)",
            "Własny rozmiar"
        });
        grpPill.Controls.Add(cmbPillPreset);

        Button btnResetPos = new Button
        {
            Text = "📍 Resetuj pozycję",
            Left = 354,
            Top = 24,
            Width = 170,
            Height = 27,
            BackColor = Color.FromArgb(241, 245, 249),
            FlatStyle = FlatStyle.Flat,
            Font = fReg
        };
        btnResetPos.Click += (s, e) =>
        {
            tempPillX = -1;
            tempPillY = -1;
            PreviewPillNow();
        };
        grpPill.Controls.Add(btnResetPos);

        grpPill.Controls.Add(new Label { Text = "Szerokość (px):", Left = 14, Top = 64, Width = 102, Height = 22, Font = fReg });
        numPillW = new NumericUpDown { Left = 118, Top = 61, Width = 72, Minimum = 160, Maximum = 620, Increment = 10, Font = fReg };
        grpPill.Controls.Add(numPillW);

        grpPill.Controls.Add(new Label { Text = "Wysokość (px):", Left = 206, Top = 64, Width = 96, Height = 22, Font = fReg });
        numPillH = new NumericUpDown { Left = 306, Top = 61, Width = 68, Minimum = 28, Maximum = 110, Increment = 2, Font = fReg };
        grpPill.Controls.Add(numPillH);

        grpPill.Controls.Add(new Label { Text = "Krycie (%):", Left = 392, Top = 64, Width = 74, Height = 22, Font = fReg });
        numPillOpacity = new NumericUpDown { Left = 470, Top = 61, Width = 68, Minimum = 45, Maximum = 100, Increment = 5, Font = fReg };
        grpPill.Controls.Add(numPillOpacity);

        grpPill.Controls.Add(new Label
        {
            Text = "Wskazówka: pigułkę można przesuwać myszką oraz rozciągać za prawy dolny róg (◢).",
            Left = 14,
            Top = 94,
            Width = 670,
            Height = 20,
            Font = new Font("Segoe UI", 8F, FontStyle.Regular),
            ForeColor = Color.FromArgb(100, 116, 139)
        });

        cmbPillPreset.SelectedIndexChanged += (s, e) =>
        {
            if (suppressUiEvents) return;
            suppressUiEvents = true;
            if (cmbPillPreset.SelectedIndex == 0) { numPillW.Value = 200; numPillH.Value = 34; }
            else if (cmbPillPreset.SelectedIndex == 1) { numPillW.Value = 265; numPillH.Value = 44; }
            else if (cmbPillPreset.SelectedIndex == 2) { numPillW.Value = 340; numPillH.Value = 54; }
            suppressUiEvents = false;
            PreviewPillNow();
        };

        EventHandler onDimChanged = (s, e) =>
        {
            if (suppressUiEvents) return;
            suppressUiEvents = true;
            SyncPresetDropdownFromNumbers();
            suppressUiEvents = false;
            PreviewPillNow();
        };
        numPillW.ValueChanged += onDimChanged;
        numPillH.ValueChanged += onDimChanged;
        numPillOpacity.ValueChanged += (s, e) => { if (!suppressUiEvents) PreviewPillNow(); };

        y += 130;

        txtTestStatus = new TextBox
        {
            Left = 20,
            Top = y,
            Width = 700,
            Height = 76,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = Color.FromArgb(37, 99, 235),
            Font = new Font("Segoe UI", 8.8F),
            Text = "Wpisz klucz Google AI Studio (lub Groq) i kliknij \"🧪 Testuj połączenie AI\". Udany test od razu zapisze i aktywuje klucz."
        };
        this.Controls.Add(txtTestStatus);
        y += 86;

        btnTest = new Button
        {
            Text = "🧪 Testuj połączenie AI",
            Left = 20,
            Top = y,
            Width = 225,
            Height = 36,
            BackColor = Color.FromArgb(226, 232, 240),
            FlatStyle = FlatStyle.Flat
        };
        btnTest.Click += (s, e) => TestAi();
        this.Controls.Add(btnTest);

        Button btnCancel = new Button
        {
            Text = "Anuluj",
            Left = 400,
            Top = y,
            Width = 110,
            Height = 36,
            BackColor = Color.FromArgb(241, 245, 249),
            FlatStyle = FlatStyle.Flat
        };
        btnCancel.Click += (s, e) => CancelAndHide();
        this.Controls.Add(btnCancel);

        Button btnSave = new Button
        {
            Text = "💾 Zapisz ustawienia",
            Left = 520,
            Top = y,
            Width = 200,
            Height = 36,
            BackColor = Color.FromArgb(37, 99, 235),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        btnSave.Click += (s, e) => SaveAndClose();
        this.Controls.Add(btnSave);

        this.FormClosing += (s, e) =>
        {
            if (!saved && context != null)
            {
                context.RefreshUiAfterSettings();
            }
        };

        SyncFromConfig();
    }

    public void SyncFromConfig()
    {
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(SyncFromConfig)); } catch { }
            return;
        }

        saved = false;
        AiConfig cur = SpacjomatContext.aiConfig ?? AiConfig.Load();
        cur.Clamp();
        tempPillX = cur.PillX;
        tempPillY = cur.PillY;

        suppressUiEvents = true;
        try
        {
            chkEnableAi.Checked = cur.EnableAi;
            chkShowPill.Checked = cur.ShowPill;
            chkFastHedge.Checked = cur.FastHedgeMode;
            cmbCorrectionMode.SelectedIndex = (cur.CorrectionMode == "auto") ? 1 : 0;

            string epLow = (cur.CustomEndpoint ?? "").ToLowerInvariant();
            if (cur.Provider != "openai")
            {
                cmbProvider.SelectedIndex = 0;
            }
            else if (epLow.Contains("groq.com"))
            {
                cmbProvider.SelectedIndex = 1;
            }
            else if (epLow.Contains("localhost") || epLow.Contains("127.0.0.1"))
            {
                cmbProvider.SelectedIndex = 3;
            }
            else
            {
                cmbProvider.SelectedIndex = 2;
            }

            txtApiKey.Text = cur.ApiKey ?? "";
            txtEndpoint.Text = cur.CustomEndpoint ?? "";

            string curModel = string.IsNullOrEmpty(cur.Model) ? "gemini-3.1-flash-lite" : cur.Model;
            if (!cmbModel.Items.Contains(curModel)) cmbModel.Items.Insert(0, curModel);
            cmbModel.Text = curModel;

            if (cur.Style == "business") cmbStyle.SelectedIndex = 1;
            else if (cur.Style == "strict") cmbStyle.SelectedIndex = 2;
            else cmbStyle.SelectedIndex = 0;

            numDailyLimit.Value = Math.Max(20, Math.Min(100000, cur.DailyLimitEstimate));
            numRpmLimit.Value = Math.Max(1, Math.Min(5000, cur.RpmLimitEstimate));

            numPillW.Value = Math.Max(160, Math.Min(620, cur.PillWidth));
            numPillH.Value = Math.Max(28, Math.Min(110, cur.PillHeight));
            numPillOpacity.Value = Math.Max(45, Math.Min(100, cur.PillOpacity));
            SyncPresetDropdownFromNumbers();
        }
        finally
        {
            suppressUiEvents = false;
        }

        RefreshUsageView();
    }

    private void OnProviderPresetChanged()
    {
        if (suppressUiEvents) return;
        int idx = cmbProvider.SelectedIndex;
        if (idx == 0)
        {
            if (txtEndpoint.Text.Contains("groq.com") || txtEndpoint.Text.Contains("localhost")) txtEndpoint.Text = "";
            if (!cmbModel.Text.StartsWith("gemini")) cmbModel.Text = "gemini-3.1-flash-lite";
        }
        else if (idx == 1)
        {
            txtEndpoint.Text = "https://api.groq.com/openai/v1/chat/completions";
            cmbModel.Text = "llama-3.3-70b-versatile";
            if ((int)numDailyLimit.Value == 250) numDailyLimit.Value = 1000;
            if ((int)numRpmLimit.Value == 15) numRpmLimit.Value = 30;
        }
        else if (idx == 2)
        {
            if (txtEndpoint.Text.Contains("groq.com") || txtEndpoint.Text.Contains("localhost")) txtEndpoint.Text = "";
            if (cmbModel.Text.StartsWith("gemini") || cmbModel.Text.StartsWith("llama-3.3")) cmbModel.Text = "gpt-4o-mini";
        }
        else if (idx == 3)
        {
            txtEndpoint.Text = "http://localhost:11434/v1/chat/completions";
            cmbModel.Text = "llama3.2";
        }
        RefreshUsageView();
    }

    public void RefreshUsageView()
    {
        if (this.InvokeRequired)
        {
            try { this.BeginInvoke(new Action(RefreshUsageView)); } catch { }
            return;
        }

        int rpm, tpm;
        AiUsageData u = AiUsageTracker.GetSnapshot(out rpm, out tpm);

        int dayLim = (int)numDailyLimit.Value;
        int rpmLim = (int)numRpmLimit.Value;
        if (dayLim < 1) dayLim = 250;

        int used = u.RequestsToday;
        int remaining = Math.Max(0, dayLim - used);
        double pctUsed = Math.Min(100.0, (used * 100.0) / dayLim);
        int fillW = (int)Math.Round((pnlBarBg.Width * pctUsed) / 100.0);
        if (used > 0 && fillW < 4) fillW = 4;
        pnlBarFill.Width = Math.Min(pnlBarBg.Width, fillW);

        if (u.RateLimits429Today > 0 || pctUsed >= 90.0)
            pnlBarFill.BackColor = Color.FromArgb(220, 38, 38);
        else if (pctUsed >= 65.0)
            pnlBarFill.BackColor = Color.FromArgb(245, 158, 11);
        else
            pnlBarFill.BackColor = Color.FromArgb(22, 163, 74);

        if (u.HasRemoteQuota && !string.IsNullOrEmpty(u.RemoteReqRemaining))
        {
            lblDailyQuota.Text = string.Format("Zostało wg API: {0} / {1} zap.  (dzisiaj lokalnie: {2})",
                u.RemoteReqRemaining,
                string.IsNullOrEmpty(u.RemoteReqLimit) ? "?" : u.RemoteReqLimit,
                used);
        }
        else
        {
            int pctLeft = Math.Max(0, 100 - (int)Math.Round(pctUsed));
            lblDailyQuota.Text = string.Format("Dzisiaj: {0} / {1} zap.  (zostało ok. {2} • {3}% wolne)",
                used, dayLim, remaining, pctLeft);
        }

        lblRpmAndTokens.Text = string.Format(
            "Tempo (RPM): {0} / {1} zap./min  |  Tokeny dzisiaj: {2:N0} (we: {3:N0} / wy: {4:N0})  •  Błędy 429: {5}",
            rpm, rpmLim, u.TotalTokensToday, u.PromptTokensToday, u.OutputTokensToday, u.RateLimits429Today);

        var modelParts = new List<string>();
        if (u.ModelRequestsToday != null)
        {
            foreach (var kv in u.ModelRequestsToday)
            {
                int c429 = 0;
                if (u.Model429Today != null) u.Model429Today.TryGetValue(kv.Key, out c429);
                modelParts.Add(c429 > 0 ? string.Format("{0}: {1} ({2}×429)", kv.Key, kv.Value, c429) : (kv.Key + ": " + kv.Value));
            }
        }
        string modelLine = (modelParts.Count > 0) ? ("Pomocnicy dzisiaj: " + string.Join(" | ", modelParts.ToArray())) : "Pomocnicy dzisiaj: brak zapytań";

        string remoteNote;
        if (u.HasRemoteQuota && !string.IsNullOrEmpty(u.RemoteTokRemaining))
        {
            remoteNote = string.Format("Limity z serwera API: tokeny pozostałe {0} / {1} (reset: {2})",
                u.RemoteTokRemaining,
                string.IsNullOrEmpty(u.RemoteTokLimit) ? "?" : u.RemoteTokLimit,
                string.IsNullOrEmpty(u.RemoteReqReset) ? "-" : u.RemoteReqReset);
        }
        else if (cmbProvider.SelectedIndex == 0)
        {
            remoteNote = "Gemini nie wysyła pozostałego limitu w API — limity są wspólne na projekt Google Cloud (Lite ~500/dzień, Flash ~20/dzień, reset ~09:00 PL). Sprawdź dokładne wartości obok.";
        }
        else
        {
            remoteNote = string.IsNullOrEmpty(u.LastStatusText)
                ? "Oczekiwanie na pierwsze zapytanie AI..."
                : ("Ostatni status (" + u.LastUpdatedTime + "): " + u.LastStatusText);
        }

        lblModelAndRemote.Text = modelLine + "\r\n" + remoteNote;
    }

    private void SyncPresetDropdownFromNumbers()
    {
        int w = (int)numPillW.Value;
        int h = (int)numPillH.Value;
        if (w == 200 && h == 34) cmbPillPreset.SelectedIndex = 0;
        else if (w == 265 && h == 44) cmbPillPreset.SelectedIndex = 1;
        else if (w == 340 && h == 54) cmbPillPreset.SelectedIndex = 2;
        else cmbPillPreset.SelectedIndex = 3;
    }

    private AiConfig BuildCfgFromUi()
    {
        var cfg = new AiConfig();
        cfg.EnableAi = chkEnableAi.Checked;
        cfg.ShowPill = chkShowPill.Checked;
        cfg.FastHedgeMode = chkFastHedge.Checked;
        cfg.CorrectionMode = (cmbCorrectionMode.SelectedIndex == 1) ? "auto" : "suggest";
        cfg.Provider = (cmbProvider.SelectedIndex == 0) ? "gemini" : "openai";
        cfg.ApiKey = AiConfig.SanitizeKey(txtApiKey.Text);
        cfg.Model = cmbModel.Text.Trim();
        cfg.CustomEndpoint = txtEndpoint.Text.Trim();
        if (cmbStyle.SelectedIndex == 1) cfg.Style = "business";
        else if (cmbStyle.SelectedIndex == 2) cfg.Style = "strict";
        else cfg.Style = "natural";

        cfg.DailyLimitEstimate = (int)numDailyLimit.Value;
        cfg.RpmLimitEstimate = (int)numRpmLimit.Value;

        if (cmbPillPreset.SelectedIndex == 0) cfg.PillSizePreset = "compact";
        else if (cmbPillPreset.SelectedIndex == 1) cfg.PillSizePreset = "medium";
        else if (cmbPillPreset.SelectedIndex == 2) cfg.PillSizePreset = "large";
        else cfg.PillSizePreset = "custom";

        cfg.PillWidth = (int)numPillW.Value;
        cfg.PillHeight = (int)numPillH.Value;
        cfg.PillOpacity = (int)numPillOpacity.Value;
        cfg.PillX = tempPillX;
        cfg.PillY = tempPillY;
        cfg.Clamp();
        return cfg;
    }

    private void PreviewPillNow()
    {
        if (context == null) return;
        context.PreviewPill(BuildCfgFromUi());
    }

    private void DetectModels()
    {
        if (cmbProvider.SelectedIndex == 1)
        {
            cmbModel.Items.Clear();
            cmbModel.Items.AddRange(new object[] { "llama-3.3-70b-versatile", "openai/gpt-oss-120b", "llama-3.1-8b-instant", "qwen/qwen3-32b" });
            cmbModel.Text = "llama-3.3-70b-versatile";
            txtTestStatus.ForeColor = Color.FromArgb(37, 99, 235);
            txtTestStatus.Text = "Załadowano najlepsze darmowe modele Groq do języka polskiego (zalecany: llama-3.3-70b-versatile). Wpisz klucz z console.groq.com/keys i kliknij Test.";
            return;
        }
        if (cmbProvider.SelectedIndex >= 2)
        {
            txtTestStatus.ForeColor = Color.FromArgb(37, 99, 235);
            txtTestStatus.Text = "Dla OpenAI / Ollamy wpisz nazwę modelu ręcznie (np. gpt-4o-mini, llama3.2, bielik).";
            return;
        }
        string key = AiConfig.SanitizeKey(txtApiKey.Text);
        if (string.IsNullOrEmpty(key))
        {
            txtTestStatus.ForeColor = Color.Crimson;
            txtTestStatus.Text = "Wpisz najpierw klucz API z aistudio.google.com/apikey!";
            return;
        }

        btnDetectModels.Enabled = false;
        this.Cursor = Cursors.WaitCursor;
        txtTestStatus.ForeColor = Color.FromArgb(37, 99, 235);
        txtTestStatus.Text = "Łączę z Google AI Studio i sprawdzam dostępne modele dla Twojego klucza...";
        Application.DoEvents();

        try
        {
            string err;
            List<string> models = AiCorrectionEngine.DiscoverGeminiModels(key, true, out err);
            if (models != null && models.Count > 0)
            {
                string currentTyped = cmbModel.Text.Trim();
                cmbModel.Items.Clear();
                foreach (string m in models)
                {
                    cmbModel.Items.Add(m);
                }
                string chosen = models.Contains(currentTyped) ? currentTyped : models[0];
                cmbModel.Text = chosen;

                txtTestStatus.ForeColor = Color.FromArgb(22, 163, 74);
                txtTestStatus.Text = string.Format(
                    "✅ Wykryto {0} dostępnych modeli! Wybrano: {1}\r\nDostępne: {2}",
                    models.Count,
                    chosen,
                    string.Join(", ", models.ToArray()));
            }
            else
            {
                txtTestStatus.ForeColor = Color.Crimson;
                txtTestStatus.Text = "❌ Nie udało się pobrać listy modeli:\r\n" + (err ?? "brak szczegółów") +
                    "\r\nWskazówka: Sprawdź, czy klucz z Google AI Studio został wklejony w całości i nie wygasł.";
            }
        }
        finally
        {
            btnDetectModels.Enabled = true;
            this.Cursor = Cursors.Default;
        }
    }

    private void TestAi()
    {
        var cfg = BuildCfgFromUi();
        if (!AiCorrectionEngine.IsAiReady(cfg))
        {
            txtTestStatus.ForeColor = Color.Crimson;
            txtTestStatus.Text = "Wpisz najpierw klucz API (lub URL lokalnej Ollamy)!";
            return;
        }

        btnTest.Enabled = false;
        this.Cursor = Cursors.WaitCursor;
        txtTestStatus.ForeColor = Color.FromArgb(37, 99, 235);
        txtTestStatus.Text = "Wysyłam próbne zdanie do AI (" + (cfg.Model ?? "gemini-3.1-flash-lite") + ")...";
        Application.DoEvents();

        try
        {
            string err;
            long ms;
            string sampleInput = "Czemu wyświetla sie całość tyldo u coiemiue?";
            string okSummary = null;

            if (cfg.CorrectionMode == "suggest")
            {
                AiSuggestionResult sug = AiCorrectionEngine.SuggestWithAi(sampleInput, cfg, out err, out ms);
                if (sug != null && sug.Options != null && sug.Options.Count > 0)
                {
                    var lines = new List<string>();
                    lines.Add(string.Format("✅ Połączenie działa i ZAPISANO klucz! ({0} ms, model: {1})", ms, cfg.Model));
                    if (sug.Fixes.Count > 0) lines.Add("Wykryte błędy: " + string.Join(", ", sug.Fixes.ToArray()));
                    for (int i = 0; i < Math.Min(2, sug.Options.Count); i++)
                    {
                        lines.Add(string.Format("{0}. [{1}] {2}", i + 1, sug.Options[i].Label, sug.Options[i].Text));
                    }
                    okSummary = string.Join("\r\n", lines.ToArray());
                }
            }
            else
            {
                string res = AiCorrectionEngine.CorrectWithAi(sampleInput, cfg, out err, out ms);
                if (!string.IsNullOrEmpty(res))
                {
                    okSummary = string.Format(
                        "✅ Połączenie działa i ZAPISANO klucz! ({0} ms, model: {1})\r\nPrzykładowa poprawka:\r\n{2}",
                        ms,
                        cfg.Model,
                        res);
                }
            }

            if (!string.IsNullOrEmpty(okSummary))
            {
                if (!string.IsNullOrEmpty(cfg.Model) && cmbModel.Text.Trim() != cfg.Model)
                {
                    cmbModel.Text = cfg.Model;
                }

                // Commit tested & working credentials immediately so moving/resizing the pill or closing with [X] never loses the key!
                if (SpacjomatContext.aiConfig == null) SpacjomatContext.aiConfig = cfg;
                else SpacjomatContext.aiConfig.CopyAiFieldsFrom(cfg);
                SpacjomatContext.aiConfig.Save();
                saved = true;
                if (context != null) context.RefreshUiAfterSettings();

                txtTestStatus.ForeColor = Color.FromArgb(22, 163, 74);
                txtTestStatus.Text = okSummary;
            }
            else
            {
                string safeErr = err ?? "nieznany błąd";
                string hint = "";
                string low = safeErr.ToLowerInvariant();
                if (low.Contains("api_key") || low.Contains("api key") || low.Contains("401") || low.Contains("403") || low.Contains("permission"))
                {
                    hint = "\r\nWskazówka: Sprawdź, czy wkleiłeś cały klucz z aistudio.google.com/apikey (lub Groq) bez spacji i czy klucz jest aktywny.";
                }
                else if (low.Contains("404") || low.Contains("not found"))
                {
                    hint = "\r\nWskazówka: Kliknij \"🔍 Wykryj modele\" powyżej, aby automatycznie wybrać model dostępny na Twoim kluczu.";
                }
                else if (low.Contains("429") || low.Contains("quota") || low.Contains("resource_exhausted"))
                {
                    hint = "\r\nWskazówka: Limit darmowych zapytań dla tego modelu został chwilowo przekroczony — kliknij \"🔍 Wykryj modele\" i wybierz inny model Gemini lub darmowe Groq.";
                }
                txtTestStatus.ForeColor = Color.Crimson;
                txtTestStatus.Text = "❌ Błąd połączenia:\r\n" + safeErr + hint;
            }
        }
        finally
        {
            RefreshUsageView();
            btnTest.Enabled = true;
            this.Cursor = Cursors.Default;
        }
    }

    private void CancelAndHide()
    {
        saved = false;
        if (context != null) context.RefreshUiAfterSettings();
        this.Hide();
    }

    private void SaveAndClose()
    {
        saved = true;
        SpacjomatContext.aiConfig = BuildCfgFromUi();
        SpacjomatContext.aiConfig.Save();
        AiCorrectionEngine.WarmUpConnectionAsync(SpacjomatContext.aiConfig);
        if (context != null) context.RefreshUiAfterSettings();
        this.Hide();
    }
}

// -------------------------------------------------------------
// Spacjomat Studio Form
// -------------------------------------------------------------
public class SpacjomatStudioForm : Form
{
    private RichTextBox txtInput;
    private RichTextBox txtOutput;
    private Label lblStats;
    private SpacjomatContext context;

    public SpacjomatStudioForm(Icon icon, SpacjomatContext ctx)
    {
        this.context = ctx;
        this.Text = "Spacjomat Studio — Inteligencja Grammarly + Baza 4.5M Słów";
        this.Icon = icon;
        this.Width = 1040;
        this.Height = 680;
        this.StartPosition = FormStartPosition.CenterScreen;
        this.BackColor = Color.FromArgb(248, 250, 252);
        this.Font = new Font("Segoe UI", 10F, FontStyle.Regular);

        InitializeComponents();
    }

    private void InitializeComponents()
    {
        Panel headerPanel = new Panel();
        headerPanel.Dock = DockStyle.Top;
        headerPanel.Height = 70;
        headerPanel.BackColor = Color.FromArgb(30, 41, 59);
        this.Controls.Add(headerPanel);

        Label lblTitle = new Label();
        lblTitle.Text = "SPACJOMAT STUDIO (HYBRYDOWY SILNIK AI + 4.5 MLN SŁÓW)";
        lblTitle.ForeColor = Color.White;
        lblTitle.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        lblTitle.Location = new Point(20, 12);
        lblTitle.AutoSize = true;
        headerPanel.Controls.Add(lblTitle);

        Label lblSubtitle = new Label();
        lblSubtitle.Text = "Korekta logiki i sensu całej wypowiedzi, literówek, interpunkcji oraz baz Google GBoard + SJP + LanguageTool";
        lblSubtitle.ForeColor = Color.FromArgb(203, 213, 225);
        lblSubtitle.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        lblSubtitle.Location = new Point(20, 38);
        lblSubtitle.AutoSize = true;
        headerPanel.Controls.Add(lblSubtitle);

        Panel toolbar = new Panel();
        toolbar.Dock = DockStyle.Top;
        toolbar.Height = 54;
        toolbar.BackColor = Color.FromArgb(241, 245, 249);
        toolbar.Padding = new Padding(15, 8, 15, 8);
        this.Controls.Add(toolbar);

        Button btnAi = CreateStyledButton("🧠 Popraw z AI (Logika / Ctrl+Enter)", Color.FromArgb(37, 99, 235), Color.White);
        btnAi.Location = new Point(15, 10);
        btnAi.Width = 250;
        btnAi.Click += (s, e) => RunCorrection(true);
        toolbar.Controls.Add(btnAi);

        Button btnOffline = CreateStyledButton("⚡ Popraw Offline", Color.FromArgb(15, 118, 110), Color.White);
        btnOffline.Location = new Point(275, 10);
        btnOffline.Width = 155;
        btnOffline.Click += (s, e) => RunCorrection(false);
        toolbar.Controls.Add(btnOffline);

        Button btnPaste = CreateStyledButton("📋 Wklej i popraw", Color.FromArgb(226, 232, 240), Color.FromArgb(15, 23, 42));
        btnPaste.Location = new Point(440, 10);
        btnPaste.Width = 150;
        btnPaste.Click += (s, e) => PasteAndCorrect();
        toolbar.Controls.Add(btnPaste);

        Button btnCopy = CreateStyledButton("📑 Skopiuj wynik", Color.FromArgb(226, 232, 240), Color.FromArgb(15, 23, 42));
        btnCopy.Location = new Point(600, 10);
        btnCopy.Width = 135;
        btnCopy.Click += (s, e) => CopyResult();
        toolbar.Controls.Add(btnCopy);

        Button btnSettings = CreateStyledButton("⚙️ Ustawienia AI", Color.FromArgb(226, 232, 240), Color.FromArgb(15, 23, 42));
        btnSettings.Location = new Point(745, 10);
        btnSettings.Width = 135;
        btnSettings.Click += (s, e) => { if (context != null) context.OpenAiSettings(); };
        toolbar.Controls.Add(btnSettings);

        Button btnClear = CreateStyledButton("🧹 Wyczyść", Color.FromArgb(226, 232, 240), Color.FromArgb(100, 116, 139));
        btnClear.Location = new Point(890, 10);
        btnClear.Width = 110;
        btnClear.Click += (s, e) => { txtInput.Clear(); txtOutput.Clear(); lblStats.Text = "Gotowy."; };
        toolbar.Controls.Add(btnClear);

        Panel statusPanel = new Panel();
        statusPanel.Dock = DockStyle.Bottom;
        statusPanel.Height = 36;
        statusPanel.BackColor = Color.FromArgb(241, 245, 249);
        statusPanel.Padding = new Padding(15, 8, 15, 8);
        this.Controls.Add(statusPanel);

        lblStats = new Label();
        lblStats.Text = "Wskazówka: W dowolnym czacie wciśnij F7 (całe pole) albo zaznacz tekst i wciśnij F8 / Ctrl+Shift+K!";
        lblStats.ForeColor = Color.FromArgb(71, 85, 105);
        lblStats.Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        lblStats.Dock = DockStyle.Fill;
        statusPanel.Controls.Add(lblStats);

        SplitContainer split = new SplitContainer();
        split.Dock = DockStyle.Fill;
        split.Orientation = Orientation.Vertical;
        split.SplitterDistance = 490;
        split.SplitterWidth = 6;
        this.Controls.Add(split);
        split.BringToFront();

        Panel leftBox = new Panel();
        leftBox.Dock = DockStyle.Fill;
        leftBox.Padding = new Padding(15, 10, 5, 10);
        split.Panel1.Controls.Add(leftBox);

        Label lblInHeader = new Label();
        lblInHeader.Text = "TEKST ORYGINALNY:";
        lblInHeader.Dock = DockStyle.Top;
        lblInHeader.Height = 24;
        lblInHeader.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        lblInHeader.ForeColor = Color.FromArgb(100, 116, 139);
        leftBox.Controls.Add(lblInHeader);

        txtInput = new RichTextBox();
        txtInput.Dock = DockStyle.Fill;
        txtInput.BorderStyle = BorderStyle.FixedSingle;
        txtInput.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
        txtInput.BackColor = Color.White;
        txtInput.ForeColor = Color.FromArgb(15, 23, 42);
        txtInput.KeyDown += (s, e) => {
            if (e.Control && e.KeyCode == Keys.Enter) { RunCorrection(true); e.SuppressKeyPress = true; }
        };
        leftBox.Controls.Add(txtInput);
        txtInput.BringToFront();

        Panel rightBox = new Panel();
        rightBox.Dock = DockStyle.Fill;
        rightBox.Padding = new Padding(5, 10, 15, 10);
        split.Panel2.Controls.Add(rightBox);

        Label lblOutHeader = new Label();
        lblOutHeader.Text = "POPRAWIONY TEKST (LOGIKA, SENS, ORTOGRAFIA):";
        lblOutHeader.Dock = DockStyle.Top;
        lblOutHeader.Height = 24;
        lblOutHeader.Font = new Font("Segoe UI", 8.5F, FontStyle.Bold);
        lblOutHeader.ForeColor = Color.FromArgb(37, 99, 235);
        rightBox.Controls.Add(lblOutHeader);

        txtOutput = new RichTextBox();
        txtOutput.Dock = DockStyle.Fill;
        txtOutput.BorderStyle = BorderStyle.FixedSingle;
        txtOutput.Font = new Font("Segoe UI", 11F, FontStyle.Regular);
        txtOutput.BackColor = Color.FromArgb(248, 250, 252);
        txtOutput.ForeColor = Color.FromArgb(15, 23, 42);
        rightBox.Controls.Add(txtOutput);
        txtOutput.BringToFront();
    }

    private Button CreateStyledButton(string text, Color bg, Color fg)
    {
        Button btn = new Button();
        btn.Text = text;
        btn.BackColor = bg;
        btn.ForeColor = fg;
        btn.FlatStyle = FlatStyle.Flat;
        btn.FlatAppearance.BorderSize = 0;
        btn.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        btn.Cursor = Cursors.Hand;
        btn.Height = 34;
        return btn;
    }

    public void SetInputText(string text)
    {
        txtInput.Text = text;
        RunCorrection(true);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            this.Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    private void RunCorrection(bool preferAi)
    {
        string raw = txtInput.Text;
        if (string.IsNullOrWhiteSpace(raw)) return;

        if (preferAi && AiCorrectionEngine.IsAiReady(SpacjomatContext.aiConfig))
        {
            lblStats.Text = "🧠 AI analizuje sens i logikę tekstu...";
            ThreadPool.QueueUserWorkItem(s => {
                string err;
                long ms;
                string aiRes = AiCorrectionEngine.CorrectWithAi(raw, SpacjomatContext.aiConfig, out err, out ms);
                this.BeginInvoke(new Action(() => {
                    if (!string.IsNullOrEmpty(aiRes))
                    {
                        txtOutput.Text = aiRes;
                        lblStats.Text = string.Format("🧠 AI Poprawiono logicznie cały tekst ({0} ms)", ms);
                    }
                    else
                    {
                        RunOfflineInternal(raw, " (AI niedostępne: " + err + " -> użyto bazy Offline)");
                    }
                }));
            });
            return;
        }

        RunOfflineInternal(raw, preferAi && !AiCorrectionEngine.IsAiReady(SpacjomatContext.aiConfig) ? " (Skonfiguruj klucz Gemini w Ustawieniach AI, aby włączyć pełne rozumienie sensu!)" : "");
    }

    private void RunOfflineInternal(string raw, string suffix)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int words, commas, sentences;
        string result = SpacjomatContext.CorrectFullTextStructure(raw, out words, out commas, out sentences);
        sw.Stop();

        txtOutput.Text = result;
        lblStats.Text = string.Format("⚡ Offline: Poprawiono {0} słów | +{1} przecinków | {2} zdań ({3} ms){4}",
            words, commas, sentences, sw.ElapsedMilliseconds, suffix);
    }

    private void PasteAndCorrect()
    {
        ThreadPool.QueueUserWorkItem(s => {
            string t = null;
            Thread sta = new Thread(() => {
                try { if (Clipboard.ContainsText()) t = Clipboard.GetText(); } catch { }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join(300);

            if (!string.IsNullOrEmpty(t))
            {
                this.BeginInvoke(new Action(() => {
                    txtInput.Text = t;
                    RunCorrection(true);
                }));
            }
        });
    }

    private void CopyResult()
    {
        if (!string.IsNullOrEmpty(txtOutput.Text))
        {
            string t = txtOutput.Text;
            Thread sta = new Thread(() => {
                try { Clipboard.SetText(t); } catch { }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join(300);

            lblStats.Text = "📋 Poprawiony tekst został skopiowany do schowka!";
        }
    }
}
