// ReplayTags.cs - bookmarks ("tags", "moments") in a replay: the hand-editable <name>.tags.json sidecar, anchor
// resolution (match time, round time, barrier time) and auto highlights (clutches, aces, multikills, first bloods).
// Drop into Unity next to VrpReplay.cs. No other dependencies (the sidecar JSON reader/writer is built in).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ValorantReplay.Playback
{
    /// <summary>User = from the sidecar or added at runtime; Auto = computed by <see cref="Highlights.Detect"/> (never saved
    /// to the sidecar, regenerated on every conversion / retag).</summary>
    public enum TagSource : byte { User = 0, Auto = 1 }

    /// <summary>A resolved bookmark: a point (Duration 0) or a range in match time.</summary>
    public sealed class ReplayTag
    {
        public float Time;                     // match seconds
        public float Duration;                 // 0 = a point
        public short Round = -1;               // 0-based index into VrpReplay.Rounds; -1 = none
        public byte Player = 255;              // player index; 255 = none
        public string Label = "", Note = "";
        public List<string> Categories = new List<string>();   // the sidecar's "tags", e.g. "util"
        public TagSource Source;
        public string Kind = "";               // auto: "Clutch", "Ace", "MultiKill", "FirstBlood"; user: ""
        /// <summary>The anchor as written in the sidecar (e.g. "round=7 barrierTime=-4"); "" = plain match time.
        /// See <see cref="TagAnchor.Parse"/>.</summary>
        public string Anchor = "";

        public float EndTime => Time + Duration;
        public bool IsAuto => Source == TagSource.Auto;

        /// <summary>A range tag is active over [Time, EndTime); a point tag for pointWindow seconds from Time.</summary>
        public bool ActiveAt(float time, float pointWindow = 1f) => time >= Time && time < Time + Math.Max(Duration, pointWindow);

        /// <summary>Turns an auto tag into a user tag so it is saved by <see cref="VrpReplay.ToSidecar"/> (call it before
        /// editing an auto tag). Its Kind moves into Categories (e.g. "clutch"), which also keeps the regenerated auto
        /// tag from duplicating it.</summary>
        public void MakeUserTag(VrpReplay replay)
        {
            if (Source == TagSource.User) return;
            string cat = Kind.ToLowerInvariant();
            if (cat.Length > 0 && !Categories.Contains(cat)) Categories.Add(cat);
            Source = TagSource.User;
            Kind = "";
            Anchor = (Round >= 0 && replay != null && Round < replay.Rounds.Count
                ? TagAnchor.InRound(Round, Time - replay.Rounds[Round].StartTime) : TagAnchor.AtTime(Time)).ToString();
        }

        public override string ToString() => $"{TagAnchor.FormatClock(Time)}{(Duration > 0 ? "+" + Duration.ToString("0.#", CultureInfo.InvariantCulture) + "s" : "")} {Label}";
    }

    public enum TagAnchorKind : byte { Match = 0, Round = 1, Barrier = 2 }

    /// <summary>Where a tag sits: match time, round + time from the round start, or round + time from the barrier drop
    /// (may be negative). Offset is in seconds; Text keeps a clock string ("0:23") as written so a save writes it back.</summary>
    public sealed class TagAnchor
    {
        public TagAnchorKind Kind;
        /// <summary>1-based round number, as the game shows it (Rounds[Round - 1]); 0 for match-time anchors.</summary>
        public int Round;
        public float Offset;
        /// <summary>The clock string as written ("12:34.5"), or null if it was a plain number.</summary>
        public string Text;

        public static TagAnchor AtTime(float seconds) => new TagAnchor { Kind = TagAnchorKind.Match, Offset = seconds };
        /// <summary>roundIndex is 0-based (like every other VrpReplay API); it is stored as the 1-based round number.</summary>
        public static TagAnchor InRound(int roundIndex, float secondsFromStart) =>
            new TagAnchor { Kind = TagAnchorKind.Round, Round = roundIndex + 1, Offset = secondsFromStart };
        /// <summary>roundIndex is 0-based; secondsFromBarrier may be negative (buy phase).</summary>
        public static TagAnchor FromBarrier(int roundIndex, float secondsFromBarrier) =>
            new TagAnchor { Kind = TagAnchorKind.Barrier, Round = roundIndex + 1, Offset = secondsFromBarrier };

        public string Key => Kind == TagAnchorKind.Match ? "time" : Kind == TagAnchorKind.Round ? "roundTime" : "barrierTime";

        /// <summary>The anchor's match time, clamped to [0, Duration]. False (with a reason) if the round does not exist or
        /// has no barrier drop.</summary>
        public bool Resolve(VrpReplay replay, out float time, out string error)
        {
            time = 0f; error = null;
            float t;
            if (Kind == TagAnchorKind.Match) t = Offset;
            else
            {
                if (Round < 1 || Round > replay.Rounds.Count) { error = $"round {Round} does not exist (the match has {replay.Rounds.Count} rounds)"; return false; }
                var r = replay.Rounds[Round - 1];
                if (Kind == TagAnchorKind.Round) t = r.StartTime + Offset;
                else
                {
                    if (r.BarrierTime < 0) { error = $"round {Round} has no barrier drop (no combat phase)"; return false; }
                    t = r.BarrierTime + Offset;
                }
            }
            if (float.IsNaN(t)) { error = "time is not a number"; return false; }
            time = Math.Max(0f, Math.Min(replay.Duration, t));
            return true;
        }

        /// <summary>The same kind of anchor (same round, same number/clock style) moved to a new match time. Falls back to a
        /// match-time anchor if the round no longer resolves.</summary>
        public TagAnchor Rebase(VrpReplay replay, float time)
        {
            float b = 0f;
            if (Kind != TagAnchorKind.Match)
            {
                if (Round < 1 || Round > replay.Rounds.Count) return new TagAnchor { Offset = time, Text = Text != null ? FormatClock(time) : null };
                var r = replay.Rounds[Round - 1];
                if (Kind == TagAnchorKind.Round) b = r.StartTime;
                else if (r.BarrierTime >= 0) b = r.BarrierTime;
                else return new TagAnchor { Offset = time, Text = Text != null ? FormatClock(time) : null };
            }
            float off = (float)Math.Round(time - b, 2);
            return new TagAnchor { Kind = Kind, Round = Round, Offset = off, Text = Text != null ? FormatClock(off) : null };
        }

        /// <summary>"time=12:34.5", "round=7 roundTime=0:23", "round=7 barrierTime=-4".</summary>
        public override string ToString()
        {
            string v = Text ?? FormatNumber(Offset);
            return Kind == TagAnchorKind.Match ? $"time={v}" : $"round={Round} {Key}={v}";
        }

        /// <summary>Parses <see cref="ToString"/> output; null if it is not an anchor.</summary>
        public static TagAnchor Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var a = new TagAnchor();
            bool haveValue = false;
            foreach (var part in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) return null;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                if (k == "round") { if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out a.Round)) return null; continue; }
                if (k == "time") a.Kind = TagAnchorKind.Match;
                else if (k == "roundTime") a.Kind = TagAnchorKind.Round;
                else if (k == "barrierTime") a.Kind = TagAnchorKind.Barrier;
                else return null;
                if (!TryParseClock(v, out a.Offset)) return null;
                a.Text = IsPlainNumber(v) ? null : v;
                haveValue = true;
            }
            if (!haveValue) return null;
            if (a.Kind == TagAnchorKind.Match) a.Round = 0;
            return a;
        }

        /// <summary>Seconds from "ss", "m:ss(.f)" or "h:mm:ss(.f)", with an optional leading '-'. Components after the first
        /// must be below 60.</summary>
        public static bool TryParseClock(string s, out float seconds)
        {
            seconds = 0f;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            bool neg = s.StartsWith("-");
            if (neg || s.StartsWith("+")) s = s.Substring(1);
            var parts = s.Split(':');
            if (parts.Length > 3) return false;
            double total = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i];
                bool last = i == parts.Length - 1;
                if (p.Length == 0) return false;
                foreach (char c in p) if (!(char.IsDigit(c) || (last && c == '.'))) return false;
                if (!double.TryParse(p, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double v)) return false;
                if (i > 0 && v >= 60) return false;
                total = total * 60 + v;
            }
            seconds = (float)(neg ? -total : total);
            return true;
        }

        /// <summary>"m:ss" / "m:ss.f" (or "h:mm:ss"), with '-' for negative values.</summary>
        public static string FormatClock(float seconds)
        {
            string sign = seconds < 0 ? "-" : "";
            double t = Math.Round(Math.Abs((double)seconds), 2);
            int whole = (int)Math.Floor(t);
            double frac = t - whole;
            int h = whole / 3600, m = whole / 60 % 60, s = whole % 60;
            string fs = frac > 0.004 ? frac.ToString(".##", CultureInfo.InvariantCulture) : "";
            return h > 0 ? $"{sign}{h}:{m:00}:{s:00}{fs}" : $"{sign}{m}:{s:00}{fs}";
        }

        internal static string FormatNumber(float v) => ((double)v).ToString("0.###", CultureInfo.InvariantCulture);

        private static bool IsPlainNumber(string v) =>
            double.TryParse(v, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Auto highlight thresholds (the sidecar's "highlights" block).</summary>
    public sealed class HighlightSettings
    {
        public bool Enabled = true;
        public int ClutchMin = 2;              // tag 1vN clutches with N >= ClutchMin (0 = off)
        public bool ClutchRequireWin = true;   // only clutches the clutcher's team won
        public bool Ace = true;
        public int MultiKill = 3;              // kills by one player within MultiKillWindow seconds (0 = off)
        public float MultiKillWindow = 5f;
        public bool FirstBlood = false;

        public HighlightSettings Clone() => (HighlightSettings)MemberwiseClone();
    }

    /// <summary>One entry of the sidecar's "tags" array, unresolved.</summary>
    public sealed class TagSpec
    {
        public TagAnchor Anchor;
        public float Duration;
        public string Label = "", Note = "";
        public List<string> Categories = new List<string>();
        /// <summary>Player by PUUID (preferred) or 0-based index; "" / -1 = none.</summary>
        public string PlayerPuuid = "";
        public int PlayerIndex = -1;
    }

    /// <summary>The &lt;name&gt;.tags.json sidecar: highlight settings plus the user tags. Load, edit, Save.</summary>
    public sealed class TagSidecar
    {
        public const int Version = 1;
        public HighlightSettings Highlights = new HighlightSettings();
        public List<TagSpec> Tags = new List<TagSpec>();
        /// <summary>Problems found while parsing: entries that were skipped or fields that were ignored.</summary>
        public List<string> Warnings = new List<string>();

        /// <summary>"match.vrp" / "match.vrf" -> "match.tags.json" in the same folder.</summary>
        public static string PathFor(string replayPath) => Path.ChangeExtension(replayPath, ".tags.json");

        public static TagSidecar Load(string path) => Parse(File.ReadAllText(path));

        /// <summary>Parses sidecar JSON. Malformed JSON throws FormatException; bad entries are skipped with a Warning.
        /// Unknown fields are ignored.</summary>
        public static TagSidecar Parse(string json)
        {
            var sc = new TagSidecar();
            if (!(Json.Parse(json) is Dictionary<string, object> root)) throw new FormatException("tags JSON: the top level must be an object");
            if (root.TryGetValue("version", out var ver) && !(ver is double dv && dv == Version))
                sc.Warnings.Add($"version {Json.Show(ver)} is not {Version}; reading it anyway");
            if (root.TryGetValue("highlights", out var hl) && hl != null)
            {
                if (hl is Dictionary<string, object> h) ReadHighlights(h, sc.Highlights, sc.Warnings);
                else sc.Warnings.Add("\"highlights\" is not an object; using defaults");
            }
            if (root.TryGetValue("tags", out var tags) && tags != null)
            {
                if (!(tags is List<object> list)) sc.Warnings.Add("\"tags\" is not an array; no user tags read");
                else
                    for (int i = 0; i < list.Count; i++)
                    {
                        var spec = ReadTag(list[i], out string err);
                        if (spec != null) sc.Tags.Add(spec);
                        else sc.Warnings.Add($"tag #{i + 1}{Describe(list[i])}: {err}; skipped");
                    }
            }
            return sc;
        }

        public void Save(string path) => File.WriteAllText(path, ToJson());

        /// <summary>Hand-editable JSON: one line per tag.</summary>
        public string ToJson()
        {
            var sb = new StringBuilder();
            var h = Highlights ?? new HighlightSettings();
            sb.Append("{\n  \"version\": ").Append(Version).Append(",\n");
            sb.Append("  \"highlights\": { \"enabled\": ").Append(Json.Bool(h.Enabled))
              .Append(", \"clutchMin\": ").Append(h.ClutchMin)
              .Append(", \"clutchRequireWin\": ").Append(Json.Bool(h.ClutchRequireWin))
              .Append(", \"ace\": ").Append(Json.Bool(h.Ace))
              .Append(", \"multiKill\": ").Append(h.MultiKill)
              .Append(", \"multiKillWindow\": ").Append(TagAnchor.FormatNumber(h.MultiKillWindow))
              .Append(", \"firstBlood\": ").Append(Json.Bool(h.FirstBlood)).Append(" },\n");
            sb.Append("  \"tags\": [");
            for (int i = 0; i < Tags.Count; i++)
            {
                var t = Tags[i];
                sb.Append(i == 0 ? "\n    { " : ",\n    { ");
                var a = t.Anchor ?? TagAnchor.AtTime(0f);
                if (a.Kind != TagAnchorKind.Match) sb.Append("\"round\": ").Append(a.Round).Append(", ");
                sb.Append('"').Append(a.Key).Append("\": ").Append(a.Text != null ? Json.Quote(a.Text) : TagAnchor.FormatNumber(a.Offset));
                if (t.Duration > 0) sb.Append(", \"duration\": ").Append(TagAnchor.FormatNumber(t.Duration));
                if (!string.IsNullOrEmpty(t.Label)) sb.Append(", \"label\": ").Append(Json.Quote(t.Label));
                if (!string.IsNullOrEmpty(t.PlayerPuuid)) sb.Append(", \"player\": ").Append(Json.Quote(t.PlayerPuuid));
                else if (t.PlayerIndex >= 0) sb.Append(", \"player\": ").Append(t.PlayerIndex);
                if (t.Categories.Count > 0)
                {
                    sb.Append(", \"tags\": [");
                    for (int j = 0; j < t.Categories.Count; j++) sb.Append(j > 0 ? ", " : "").Append(Json.Quote(t.Categories[j]));
                    sb.Append(']');
                }
                if (!string.IsNullOrEmpty(t.Note)) sb.Append(", \"note\": ").Append(Json.Quote(t.Note));
                sb.Append(" }");
            }
            sb.Append(Tags.Count > 0 ? "\n  ]\n}\n" : "]\n}\n");
            return sb.ToString();
        }

        private static string Describe(object o) =>
            o is Dictionary<string, object> d && d.TryGetValue("label", out var l) && l is string s && s.Length > 0 ? $" ('{s}')" : "";

        private static void ReadHighlights(Dictionary<string, object> h, HighlightSettings s, List<string> warn)
        {
            foreach (var kv in h)
            {
                object v = kv.Value;
                bool ok = true;
                switch (kv.Key)
                {
                    case "enabled": ok = v is bool; if (ok) s.Enabled = (bool)v; break;
                    case "clutchMin": ok = v is double d1 && d1 >= 0 && d1 == Math.Floor(d1); if (ok) s.ClutchMin = (int)(double)v; break;
                    case "clutchRequireWin": ok = v is bool; if (ok) s.ClutchRequireWin = (bool)v; break;
                    case "ace": ok = v is bool; if (ok) s.Ace = (bool)v; break;
                    case "multiKill": ok = v is double d2 && d2 >= 0 && d2 == Math.Floor(d2); if (ok) s.MultiKill = (int)(double)v; break;
                    case "multiKillWindow": ok = v is double d3 && d3 > 0; if (ok) s.MultiKillWindow = (float)(double)v; break;
                    case "firstBlood": ok = v is bool; if (ok) s.FirstBlood = (bool)v; break;
                }
                if (!ok) warn.Add($"highlights.{kv.Key} = {Json.Show(v)} is not valid; using the default");
            }
        }

        private static TagSpec ReadTag(object o, out string error)
        {
            error = null;
            if (!(o is Dictionary<string, object> d)) { error = "not an object"; return null; }
            var spec = new TagSpec();
            string key = null;
            foreach (var k in new[] { "time", "roundTime", "barrierTime" })
                if (d.ContainsKey(k))
                {
                    if (key != null) { error = $"has both \"{key}\" and \"{k}\" (use exactly one anchor)"; return null; }
                    key = k;
                }
            if (key == null) { error = "no anchor (\"time\", \"roundTime\" or \"barrierTime\")"; return null; }
            if (!ReadSeconds(d[key], out float off, out string text)) { error = $"\"{key}\": {Json.Show(d[key])} is not seconds or a clock (ss, m:ss, h:mm:ss)"; return null; }
            var a = new TagAnchor { Offset = off, Text = text };
            if (key == "time") a.Kind = TagAnchorKind.Match;
            else
            {
                a.Kind = key == "roundTime" ? TagAnchorKind.Round : TagAnchorKind.Barrier;
                if (!d.TryGetValue("round", out var r) || !(r is double rd) || rd != Math.Floor(rd) || rd < 1)
                { error = $"\"{key}\" needs \"round\" (1-based round number)"; return null; }
                a.Round = (int)rd;
            }
            spec.Anchor = a;
            if (d.TryGetValue("duration", out var dur) && dur != null)
            {
                if (!ReadSeconds(dur, out spec.Duration, out _) || spec.Duration < 0) { error = $"\"duration\": {Json.Show(dur)} is not a non-negative time"; return null; }
            }
            if (d.TryGetValue("label", out var lab) && lab != null) spec.Label = lab as string ?? Json.Show(lab);
            if (d.TryGetValue("note", out var note) && note != null) spec.Note = note as string ?? Json.Show(note);
            if (d.TryGetValue("tags", out var cats) && cats != null)
            {
                if (cats is string one) spec.Categories.Add(one);
                else if (cats is List<object> cl) foreach (var c in cl) { if (c is string cs) spec.Categories.Add(cs); }
            }
            if (d.TryGetValue("player", out var p) && p != null)
            {
                if (p is double pd && pd >= 0 && pd == Math.Floor(pd)) spec.PlayerIndex = (int)pd;
                else if (p is string ps && ps.Length > 0)
                {
                    if (int.TryParse(ps, NumberStyles.None, CultureInfo.InvariantCulture, out int pi)) spec.PlayerIndex = pi;
                    else spec.PlayerPuuid = ps;
                }
            }
            return spec;
        }

        private static bool ReadSeconds(object v, out float seconds, out string text)
        {
            text = null; seconds = 0f;
            if (v is double d) { seconds = (float)d; return !double.IsNaN(d) && !double.IsInfinity(d); }
            if (v is string s && TagAnchor.TryParseClock(s, out seconds)) { text = s.Trim(); return true; }
            return false;
        }

        /// <summary>Minimal JSON reader/writer (objects -> Dictionary, arrays -> List, numbers -> double).</summary>
        private static class Json
        {
            public static string Bool(bool b) => b ? "true" : "false";

            public static string Show(object v) =>
                v == null ? "null" : v is string s ? Quote(s) : v is double d ? d.ToString("R", CultureInfo.InvariantCulture) : v is bool b ? Bool(b) : v is List<object> ? "[...]" : "{...}";

            public static string Quote(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                return sb.Append('"').ToString();
            }

            public static object Parse(string s)
            {
                int i = 0;
                var v = Value(s, ref i);
                Ws(s, ref i);
                if (i < s.Length) throw Error(s, i, "unexpected text after the JSON value");
                return v;
            }

            private static FormatException Error(string s, int i, string msg)
            {
                int line = 1;
                for (int k = 0; k < i && k < s.Length; k++) if (s[k] == '\n') line++;
                return new FormatException($"tags JSON: {msg} at line {line}");
            }

            private static void Ws(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '﻿')) i++;
            }

            private static object Value(string s, ref int i)
            {
                Ws(s, ref i);
                if (i >= s.Length) throw Error(s, i, "unexpected end");
                char c = s[i];
                if (c == '{')
                {
                    var d = new Dictionary<string, object>();
                    i++; Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        Ws(s, ref i);
                        if (i >= s.Length || s[i] != '"') throw Error(s, i, "expected a property name");
                        string k = Str(s, ref i);
                        Ws(s, ref i);
                        if (i >= s.Length || s[i] != ':') throw Error(s, i, "expected ':'");
                        i++;
                        d[k] = Value(s, ref i);
                        Ws(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == '}') { i++; return d; }
                        throw Error(s, i, "expected ',' or '}'");
                    }
                }
                if (c == '[')
                {
                    var l = new List<object>();
                    i++; Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    while (true)
                    {
                        l.Add(Value(s, ref i));
                        Ws(s, ref i);
                        if (i < s.Length && s[i] == ',') { i++; continue; }
                        if (i < s.Length && s[i] == ']') { i++; return l; }
                        throw Error(s, i, "expected ',' or ']'");
                    }
                }
                if (c == '"') return Str(s, ref i);
                if (Lit(s, ref i, "true")) return true;
                if (Lit(s, ref i, "false")) return false;
                if (Lit(s, ref i, "null")) return null;
                int st = i;
                while (i < s.Length && ("+-.eE".IndexOf(s[i]) >= 0 || char.IsDigit(s[i]))) i++;
                if (i > st && double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out double num)) return num;
                throw Error(s, st, "unexpected character");
            }

            private static bool Lit(string s, ref int i, string lit)
            {
                if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
                i += lit.Length;
                return true;
            }

            private static string Str(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++;   // opening quote
                while (i < s.Length)
                {
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) break;
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int u))
                                throw Error(s, i, "bad \\u escape");
                            sb.Append((char)u); i += 4; break;
                        default: sb.Append(e); break;   // \" \\ \/
                    }
                }
                throw Error(s, i, "unterminated string");
            }
        }
    }

    /// <summary>Auto highlights: a pure function of the replay's Kills, Rounds, Players and Frames, so vrfconv and Unity agree.</summary>
    public static class Highlights
    {
        public const string Clutch = "Clutch", Ace = "Ace", MultiKill = "MultiKill", FirstBlood = "FirstBlood";

        public static List<ReplayTag> Detect(VrpReplay r, HighlightSettings s)
        {
            var result = new List<ReplayTag>();
            if (s == null) s = new HighlightSettings();
            if (!s.Enabled) return result;
            int np = r.Players.Count;
            bool frames = r.TickCount > 0 && r.Frames.Length >= r.TickCount * np;
            float dt = r.TickRate > 0 ? 1f / r.TickRate : 0.05f;
            for (int ri = 0; ri < r.Rounds.Count; ri++)
            {
                var round = r.Rounds[ri];
                var kills = new List<Kill>();
                foreach (var k in r.Kills) if (k.Round == ri && k.Victim < np) kills.Add(k);
                if (kills.Count == 0) continue;
                kills.Sort((a, b) => a.Time.CompareTo(b.Time));
                float lastKill = kills[kills.Count - 1].Time;

                if (s.FirstBlood)
                {
                    var k = kills[0];
                    bool byPlayer = k.Killer < np;
                    result.Add(Make(r, ri, FirstBlood, k.Time, 0f, byPlayer ? k.Killer : k.Victim,
                        byPlayer ? $"First blood - {Name(r, k.Killer)}" : $"First death - {Name(r, k.Victim)}"));
                }

                if (s.ClutchMin > 0) DetectClutches(r, s, ri, round, kills, frames, dt, lastKill, result);

                // per killer: enemy kills in order
                var aces = new List<ReplayTag>();
                var byKiller = new Dictionary<byte, List<Kill>>();
                foreach (var k in kills)
                {
                    if (k.Killer >= np || r.Players[k.Killer].Team == r.Players[k.Victim].Team) continue;
                    if (!byKiller.TryGetValue(k.Killer, out var l)) byKiller[k.Killer] = l = new List<Kill>();
                    l.Add(k);
                }
                foreach (var kv in byKiller)
                {
                    byte killer = kv.Key;
                    var ks = kv.Value;
                    if (s.Ace)
                    {
                        var enemies = new HashSet<int>();
                        for (int p = 0; p < np; p++) if (r.Players[p].Team != r.Players[killer].Team) enemies.Add(p);
                        if (enemies.Count >= 2)
                        {
                            var seen = new HashSet<int>();
                            float end = -1f;
                            foreach (var k in ks)
                            {
                                seen.Add(k.Victim);
                                if (seen.IsSupersetOf(enemies)) { end = k.Time; break; }
                            }
                            if (end >= 0)
                            {
                                var ace = Make(r, ri, Ace, ks[0].Time, end - ks[0].Time, killer, $"Ace - {Name(r, killer)}");
                                aces.Add(ace);
                                result.Add(ace);
                            }
                        }
                    }
                    if (s.MultiKill > 0 && ks.Count >= s.MultiKill)
                    {
                        int bestI = 0, bestN = 0;
                        for (int i = 0, j = 0; i < ks.Count; i++)
                        {
                            if (j < i) j = i;
                            while (j + 1 < ks.Count && ks[j + 1].Time - ks[i].Time <= s.MultiKillWindow + 1e-4f) j++;
                            if (j - i + 1 > bestN) { bestN = j - i + 1; bestI = i; }
                        }
                        if (bestN >= s.MultiKill)
                        {
                            float t0 = ks[bestI].Time, t1 = ks[bestI + bestN - 1].Time;
                            bool covered = false;
                            foreach (var a in aces) if (a.Player == killer && t0 >= a.Time - 1e-4f && t1 <= a.EndTime + 1e-4f) covered = true;
                            if (!covered)
                                result.Add(Make(r, ri, MultiKill, t0, t1 - t0, killer,
                                    $"{bestN} kills in {(t1 - t0).ToString("0.0", CultureInfo.InvariantCulture)}s - {Name(r, killer)}"));
                        }
                    }
                }
            }
            result.Sort((a, b) => a.Time.CompareTo(b.Time));
            return result;
        }

        private static void DetectClutches(VrpReplay r, HighlightSettings s, int ri, Round round, List<Kill> kills, bool frames, float dt, float lastKill, List<ReplayTag> result)
        {
            int np = r.Players.Count;
            var teams = new List<byte>();
            foreach (var p in r.Players) if (!teams.Contains(p.Team)) teams.Add(p.Team);
            var done = new HashSet<byte>();
            var dead = new HashSet<int>();   // fallback without frames: victims so far
            foreach (var k in kills)
            {
                dead.Add(k.Victim);
                float at = k.Time + dt;
                var alive = new bool[np];
                for (int p = 0; p < np; p++) alive[p] = frames ? r.Sample(p, at).Alive : !dead.Contains(p);
                foreach (byte team in teams)
                {
                    if (done.Contains(team)) continue;
                    int mine = 0, enemies = 0, clutcher = -1;
                    for (int p = 0; p < np; p++)
                    {
                        if (!alive[p]) continue;
                        if (r.Players[p].Team == team) { mine++; clutcher = p; } else enemies++;
                    }
                    if (mine != 1 || enemies < s.ClutchMin) continue;
                    done.Add(team);
                    if (s.ClutchRequireWin && round.WinningTeam != team) continue;
                    float end = lastKill;
                    if (round.EndReason == RoundEndReason.Defused && round.DefuseTime >= 0) end = round.DefuseTime;
                    else if (round.EndReason == RoundEndReason.Detonated) end = DetonationTime(r, round);
                    result.Add(Make(r, ri, Clutch, k.Time, Math.Max(0f, end - k.Time), (byte)clutcher, $"1v{enemies} clutch - {Name(r, (byte)clutcher)}"));
                }
            }
        }

        private static float DetonationTime(VrpReplay r, Round round)
        {
            foreach (var e in r.Events)
                if (e.Type == MatchEventType.SpikeDetonated && e.Time >= round.StartTime && e.Time < round.EndTime) return e.Time;
            return round.EndTime;
        }

        private static ReplayTag Make(VrpReplay r, int ri, string kind, float time, float duration, byte player, string label) =>
            new ReplayTag { Time = time, Duration = duration, Round = (short)ri, Player = player, Label = label, Source = TagSource.Auto, Kind = kind };

        private static string Name(VrpReplay r, byte p) => p < r.Players.Count ? r.Players[p].DisplayName : "?";
    }
}
