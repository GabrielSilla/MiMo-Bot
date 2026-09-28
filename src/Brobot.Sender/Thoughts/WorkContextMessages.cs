using System.IO;
using System.Reflection;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Remarks about the user's day and the moment (Data/work-context.tsv,
/// embedded) — see specs/sender-thoughts.md, "WorkContextMessages". Every
/// phrase belongs to one situation group, and a group can only speak while
/// its situation holds (built from ThoughtContext below). When several
/// hold at once the most specific tier wins: combinations > triggers and
/// "just happened" moments > special dates > the always-true general
/// groups (time of day, weekday, weather) — so it reads like Peemo
/// connecting things, not reading a list. Triggers speak once a day (or
/// once per event, for "a reunião acabou"); every group walks its own
/// persisted shuffle queue so nothing repeats until the group runs out.
/// The card gating matches the Relatório: dev needs Ferramentas de Dev,
/// social/YouTube/music need Mídia, games need Jogos, meetings need
/// Notificações.
/// </summary>
internal sealed class WorkContextMessages : IThoughtSource
{
    private const string ResourceName = "Brobot.Sender.Thoughts.Data.work-context.tsv";

    private enum Tier { General = 1, SpecialDate = 2, Trigger = 3, Combination = 4 }

    /// <summary>
    /// A situation that holds right now: its group key, how specific it is,
    /// the once-a-day/once-per-event id (null for the general groups, which
    /// can speak any number of times) and the values for its placeholders.
    /// </summary>
    private sealed record Situation(string Group, Tier Tier, string? TriggerId, IReadOnlyDictionary<string, string> Values);

    private sealed record Entry(string Id, string Group, string Face, string Level, string Text);

    private static readonly TimeSpan JustHappened = TimeSpan.FromMinutes(20);

    private readonly ThoughtHistoryStore _history;
    private readonly Dictionary<string, List<Entry>> _byGroup;
    private readonly Random _rng = new();

    public string Name => "context";
    public double Weight => 40;

    public WorkContextMessages(ThoughtHistoryStore history)
    {
        _history = history;
        _byGroup = LoadEntries().GroupBy(e => e.Group).ToDictionary(g => g.Key, g => g.ToList());
    }

    public Thought? TryPick(ThoughtContext context)
    {
        string level = LevelFor(context.Mood);
        foreach (IGrouping<Tier, Situation> tier in Situations(context)
                     .Where(s => s.TriggerId == null || !_history.WasFiredToday(s.TriggerId))
                     .GroupBy(s => s.Tier)
                     .OrderByDescending(g => g.Key))
        {
            // The group that spoke last goes to the back of its tier, so the
            // same situation doesn't talk twice in a row while another one
            // that also holds has something to say.
            foreach (Situation situation in tier
                         .OrderBy(s => s.Group == _history.Data.LastContextGroup ? 1 : 0)
                         .ThenBy(_ => _rng.Next()))
            {
                if (TryPhrase(situation, level) is { } thought)
                {
                    if (situation.TriggerId != null)
                    {
                        _history.MarkFired(situation.TriggerId);
                    }
                    _history.Data.LastContextGroup = situation.Group;
                    return thought;
                }
            }
        }
        return null;
    }

    private Thought? TryPhrase(Situation situation, string moodLevel)
    {
        if (!_byGroup.TryGetValue(situation.Group, out List<Entry>? entries))
        {
            return null;
        }

        string queueKey = "ctx:" + situation.Group;
        for (int pass = 0; pass < 2; pass++)
        {
            List<string> queue = QueueFor(queueKey, entries, reshuffle: pass == 1);
            foreach (string id in queue)
            {
                Entry? entry = entries.Find(e => e.Id == id);
                if (entry == null || (entry.Level != "-" && entry.Level != moodLevel))
                {
                    continue;
                }
                // Filled-in values (a long game name, an odd character in
                // it) can still push a phrase out of what the display takes;
                // that phrase just waits for a situation it fits.
                if (ThoughtText.Accept(Fill(entry.Text, situation.Values)) is not { } text)
                {
                    continue;
                }
                queue.Remove(id);
                return new Thought(entry.Face, text, Name, situation.Group, entry.Id);
            }
        }
        return null;
    }

    private List<string> QueueFor(string key, List<Entry> entries, bool reshuffle)
    {
        if (!reshuffle && _history.Data.Queues.TryGetValue(key, out List<string>? queue))
        {
            // Phrases added to the data file after the shuffle join at a random spot.
            foreach (Entry e in entries.Where(e => !queue.Contains(e.Id) && !_history.Data.UsedAt.ContainsKey(e.Id)))
            {
                queue.Insert(_rng.Next(queue.Count + 1), e.Id);
            }
            if (queue.Count > 0)
            {
                return queue;
            }
        }
        List<string> fresh = entries.Select(e => e.Id).OrderBy(_ => _rng.Next()).ToList();
        _history.Data.Queues[key] = fresh;
        return fresh;
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach ((string key, string value) in values)
        {
            text = text.Replace("{" + key + "}", value);
        }
        return text;
    }

    // ---- Which situations hold right now ----

    private static IEnumerable<Situation> Situations(ThoughtContext c)
    {
        var none = new Dictionary<string, string>();
        DateTime now = c.Now;
        int hour = now.Hour;
        string day = now.ToString("yyyyMMdd");
        DailyReportResult r = c.Report;
        bool weekday = now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        double gameMinutes = c.GameMinutesByName.Values.Sum();

        // General: always something here, so this source never comes up empty.
        yield return new Situation("hora:" + TimeOfDay(hour), Tier.General, null, none);
        yield return new Situation("dia:" + WeekdayKey(now.DayOfWeek), Tier.General, null, none);
        if (c.Weather is { } w)
        {
            var temp = new Dictionary<string, string> { ["temp"] = w.TempC.ToString() };
            if (WeatherKey(w.Condition) is { } condition)
            {
                // A clear sky reads as "sol" only while the sun's up;
                // after dark it's its own group, so nothing says "tá sol" at 21h.
                if (condition == "sol" && hour is < 6 or >= 18)
                {
                    condition = "noite-limpa";
                }
                yield return new Situation("clima:" + condition, Tier.General, null, temp);
            }
            if (w.TempC < 15) yield return new Situation("clima:frio", Tier.General, null, temp);
            if (w.TempC >= 28) yield return new Situation("clima:calor", Tier.General, null, temp);
        }

        // Special dates — once a day each.
        foreach (string special in SpecialDates(now))
        {
            yield return new Situation("data:" + special, Tier.SpecialDate, $"data:{special}:{day}", none);
        }

        // Report triggers — once a day each, gated like the Relatório.
        if (c.MediaEnabled)
        {
            if (c.SocialMinutesBySite.Count > 0)
            {
                KeyValuePair<string, double> top = c.SocialMinutesBySite.MaxBy(kv => kv.Value);
                string site = top.Key;
                double minutes = top.Value;
                var values = new Dictionary<string, string> { ["site"] = site, ["tempo"] = Duration(minutes) };
                if (minutes >= 90) yield return new Situation("rede-social-muito", Tier.Trigger, "rede-social-muito:" + day, values);
                else if (minutes >= 30) yield return new Situation("rede-social", Tier.Trigger, "rede-social:" + day, values);
            }
            var video = new Dictionary<string, string> { ["tempo"] = Duration(r.VideoFocusedMinutes) };
            if (r.VideoFocusedMinutes >= 90) yield return new Situation("youtube-muito", Tier.Trigger, "youtube-muito:" + day, video);
            else if (r.VideoFocusedMinutes >= 30) yield return new Situation("youtube", Tier.Trigger, "youtube:" + day, video);
            double musicMinutes = r.MediaMinutes - r.VideoFocusedMinutes;
            if (musicMinutes >= 120)
            {
                yield return new Situation("musica-longa", Tier.Trigger, "musica-longa:" + day,
                    new Dictionary<string, string> { ["tempo"] = Duration(musicMinutes) });
            }
        }
        if (c.NotificationsEnabled)
        {
            if (r.MeetingMinutes >= 120)
            {
                yield return new Situation("reuniao-longa", Tier.Trigger, "reuniao-longa:" + day,
                    new Dictionary<string, string> { ["tempo"] = Duration(r.MeetingMinutes) });
            }
            if (weekday && hour >= 15 && r.MeetingMinutes < 1)
            {
                yield return new Situation("sem-reuniao", Tier.Trigger, "sem-reuniao:" + day, none);
            }
            if (c.LastMeetingEndedAt is { } ended && now - ended < JustHappened)
            {
                yield return new Situation("reuniao-acabou", Tier.Trigger, $"reuniao-acabou:{ended:yyyyMMddHHmm}", none);
            }
        }
        if (c.DevToolsEnabled)
        {
            var n = new Dictionary<string, string> { ["n"] = r.BuildFailCount.ToString() };
            if (r.BuildFailCount >= 3) yield return new Situation("build-falhando", Tier.Trigger, "build-falhando:" + day, n);
            if (r.BuildSuccessCount >= 5 && r.BuildFailCount == 0)
            {
                yield return new Situation("build-limpo", Tier.Trigger, "build-limpo:" + day,
                    new Dictionary<string, string> { ["n"] = r.BuildSuccessCount.ToString() });
            }
            if (r.CommitCount >= 5)
            {
                yield return new Situation("commits-muitos", Tier.Trigger, "commits-muitos:" + day,
                    new Dictionary<string, string> { ["n"] = r.CommitCount.ToString() });
            }
            else if (r.CommitCount >= 1)
            {
                yield return new Situation("commit-primeiro", Tier.Trigger, "commit-primeiro:" + day, none);
            }
        }
        if (c.GamesEnabled)
        {
            if (c.LastGameEndedAt is { } gameEnded && now - gameEnded < JustHappened && c.LastGameName != null)
            {
                yield return new Situation("jogo-acabou", Tier.Trigger, $"jogo-acabou:{gameEnded:yyyyMMddHHmm}",
                    new Dictionary<string, string> { ["jogo"] = c.LastGameName });
                if (weekday && gameEnded.Hour is >= 9 and < 18)
                {
                    yield return new Situation("jogo-expediente", Tier.Trigger, "jogo-expediente:" + day,
                        new Dictionary<string, string> { ["jogo"] = c.LastGameName });
                }
            }
            var played = new Dictionary<string, string> { ["tempo"] = Duration(gameMinutes) };
            if (gameMinutes >= 180) yield return new Situation("jogo-muito", Tier.Trigger, "jogo-muito:" + day, played);
            else if (gameMinutes >= 60) yield return new Situation("jogo-hora", Tier.Trigger, "jogo-hora:" + day, played);
            if (c.GameMinutesByName.Count >= 3)
            {
                yield return new Situation("jogos-varios", Tier.Trigger, "jogos-varios:" + day,
                    new Dictionary<string, string> { ["n"] = c.GameMinutesByName.Count.ToString() });
            }
        }

        // Combinations — the most specific, once a day each: two things that
        // hold at the same time, so the remark reads like Peemo connecting
        // them rather than reading a list. Every flag below carries its
        // card's gating already.
        Situation Combo(string key) => new("combo:" + key, Tier.Combination, $"combo:{key}:{day}", none);

        DayOfWeek dow = now.DayOfWeek;
        bool weekend = !weekday;
        int? temperature = c.Weather?.TempC;
        bool hot = temperature >= 28;
        bool cold = temperature < 15;
        bool rainy = c.Weather?.Condition is WeatherCondition.Rain or WeatherCondition.Storm;
        bool storm = c.Weather?.Condition == WeatherCondition.Storm;
        bool fog = c.Weather?.Condition == WeatherCondition.Fog;
        bool lateNight = hour < 5;
        bool earlyMorning = hour is >= 5 and < 8;
        bool lunch = hour is >= 12 and < 14;
        bool evening = hour >= 18;
        var specials = SpecialDates(now).ToHashSet();

        double socialTop = c.MediaEnabled && c.SocialMinutesBySite.Count > 0 ? c.SocialMinutesBySite.Values.Max() : 0;
        double videoMin = c.MediaEnabled ? r.VideoFocusedMinutes : 0;
        double musicMin = c.MediaEnabled ? r.MediaMinutes - r.VideoFocusedMinutes : 0;
        double meetings = c.NotificationsEnabled ? r.MeetingMinutes : 0;
        bool noMeetings = c.NotificationsEnabled && hour >= 15 && r.MeetingMinutes < 1;
        bool meetingJustEnded = c.NotificationsEnabled && c.LastMeetingEndedAt is { } me && now - me < JustHappened;
        int fails = c.DevToolsEnabled ? r.BuildFailCount : 0;
        bool cleanBuilds = c.DevToolsEnabled && r.BuildSuccessCount >= 5 && r.BuildFailCount == 0;
        int commits = c.DevToolsEnabled ? r.CommitCount : 0;
        double games = c.GamesEnabled ? gameMinutes : 0;
        bool gameJustEnded = c.GamesEnabled && c.LastGameEndedAt is { } ge && now - ge < JustHappened;
        bool gameEndedLastHour = c.GamesEnabled && c.LastGameEndedAt is { } gh && now - gh < TimeSpan.FromHours(1);

        // Day of the week × something
        if (dow == DayOfWeek.Friday && hot) yield return Combo("sexta-calor");
        if (dow == DayOfWeek.Monday && rainy) yield return Combo("segunda-chuva");
        if (dow == DayOfWeek.Friday && evening) yield return Combo("sexta-noite");
        if (dow == DayOfWeek.Monday && hour is >= 5 and < 9) yield return Combo("segunda-cedo");
        if (dow == DayOfWeek.Friday && fails >= 3) yield return Combo("sexta-build-quebrado");
        if (dow == DayOfWeek.Monday && weekday && noMeetings) yield return Combo("segunda-sem-reuniao");
        if (dow == DayOfWeek.Friday && noMeetings) yield return Combo("sexta-sem-reuniao");
        if (dow == DayOfWeek.Friday && evening && rainy) yield return Combo("sexta-noite-chuva");
        if (dow == DayOfWeek.Monday && games >= 60) yield return Combo("segunda-jogo");
        if (dow == DayOfWeek.Sunday && evening && (games >= 60 || gameEndedLastHour)) yield return Combo("domingo-noite-jogo");
        if (weekend && commits >= 1) yield return Combo("fim-de-semana-commit");
        if (weekend && meetings >= 30) yield return Combo("fim-de-semana-reuniao");
        if (weekend && games >= 60) yield return Combo("fim-de-semana-jogo");

        // Time of day × something
        if (lateNight && commits >= 1) yield return Combo("madrugada-commit");
        if (lateNight && gameEndedLastHour) yield return Combo("madrugada-jogo");
        if (lateNight && videoMin >= 30) yield return Combo("madrugada-youtube");
        if (lateNight && socialTop >= 30) yield return Combo("madrugada-rede-social");
        if (lateNight && musicMin >= 120) yield return Combo("madrugada-musica");
        if (lateNight && c.NotificationsEnabled && c.LastMeetingEndedAt is { } mn && now - mn < TimeSpan.FromHours(1))
            yield return Combo("madrugada-reuniao");
        if (lateNight && cold) yield return Combo("madrugada-frio");
        if (lateNight && hot) yield return Combo("madrugada-calor");
        if (earlyMorning && cold) yield return Combo("manha-cedo-frio");
        if (earlyMorning && fog) yield return Combo("manha-cedo-neblina");
        if (lunch && weekday && gameJustEnded) yield return Combo("almoco-jogo");
        if (lunch && videoMin >= 30) yield return Combo("almoco-youtube");
        if (storm && (evening || lateNight)) yield return Combo("tempestade-noite");

        // Weather × the day's numbers
        if (rainy && meetings >= 120) yield return Combo("chuva-reuniao-longa");
        if (hot && meetings >= 120) yield return Combo("calor-reuniao-longa");
        if (hot && games >= 180) yield return Combo("calor-jogo-muito");
        if (rainy && games >= 60) yield return Combo("chuva-jogo");
        if (rainy && videoMin >= 30) yield return Combo("chuva-youtube");

        // The day's numbers × each other
        if (fails >= 3 && games >= 30) yield return Combo("build-quebrado-jogo");
        if (meetingJustEnded && fails >= 3) yield return Combo("reuniao-acabou-build-quebrado");
        if (commits >= 5 && cleanBuilds) yield return Combo("dia-dev-perfeito");
        if (c.DevToolsEnabled && hour >= 16 && meetings >= 120 && commits == 0) yield return Combo("reuniao-longa-sem-commit");
        if (musicMin >= 120 && cleanBuilds) yield return Combo("musica-build-limpo");
        if (socialTop >= 30 && meetings >= 120) yield return Combo("rede-social-reuniao-longa");

        // Special date × something
        if (specials.Contains("sexta-13") && fails >= 3) yield return Combo("sexta-13-build-quebrado");
        if (specials.Contains("natal") && (commits >= 1 || meetings >= 1)) yield return Combo("natal-trabalho");
        if (specials.Contains("ano-novo") && games >= 30) yield return Combo("ano-novo-jogo");
        if (specials.Contains("inicio-mes") && dow == DayOfWeek.Monday) yield return Combo("inicio-mes-segunda");
        if (specials.Contains("fim-mes") && dow == DayOfWeek.Friday) yield return Combo("fim-mes-sexta");
    }

    private static string TimeOfDay(int hour) => hour switch
    {
        < 5 => "madrugada",
        < 8 => "manha-cedo",
        < 12 => "manha",
        < 14 => "almoco",
        < 18 => "tarde",
        _ => "noite",
    };

    private static string WeekdayKey(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "seg",
        DayOfWeek.Tuesday => "ter",
        DayOfWeek.Wednesday => "qua",
        DayOfWeek.Thursday => "qui",
        DayOfWeek.Friday => "sex",
        DayOfWeek.Saturday => "sab",
        _ => "dom",
    };

    private static string? WeatherKey(WeatherCondition condition) => condition switch
    {
        WeatherCondition.Clear => "sol",
        WeatherCondition.Cloudy => "nublado",
        WeatherCondition.Rain => "chuva",
        WeatherCondition.Storm => "tempestade",
        WeatherCondition.Fog => "neblina",
        _ => null, // no snow group: it doesn't snow here
    };

    private static IEnumerable<string> SpecialDates(DateTime now)
    {
        if (now.Month == 12 && now.Day is 24 or 25) yield return "natal";
        if ((now.Month == 12 && now.Day == 31) || (now.Month == 1 && now.Day == 1)) yield return "ano-novo";
        if (now.Month == 10 && now.Day == 31) yield return "halloween";
        if (now.DayOfWeek == DayOfWeek.Friday && now.Day == 13) yield return "sexta-13";
        if (now.DayOfYear == 256) yield return "dia-programador";
        if (now.Day == 1) yield return "inicio-mes";
        if (now.Day == DateTime.DaysInMonth(now.Year, now.Month)) yield return "fim-mes";
    }

    /// <summary>"45 min", "2h", "2h30" — short enough for the ≤75-char budget.</summary>
    private static string Duration(double minutes)
    {
        int total = (int)Math.Round(minutes);
        if (total < 60) return $"{total} min";
        int h = total / 60, m = total % 60;
        return m == 0 ? $"{h}h" : $"{h}h{m:00}";
    }

    private static string LevelFor(Mood mood) => mood switch
    {
        Mood.FimDeDia => "medio",
        Mood.Cansado => "acido",
        _ => "leve",
    };

    private static IEnumerable<Entry> LoadEntries()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            yield break;
        }

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            string[] cols = line.Split('\t');
            if (cols.Length == 5)
            {
                yield return new Entry(cols[0], cols[1], cols[2], cols[3], cols[4]);
            }
        }
    }
}
