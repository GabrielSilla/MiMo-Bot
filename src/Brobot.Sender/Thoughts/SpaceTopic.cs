using System.Text.RegularExpressions;

namespace Brobot.Sender.Thoughts;

/// <summary>
/// Decides whether a fetched text (random fact, "neste dia" event) is about
/// space, so it's shown with one of Core's two space animations instead of
/// a plain FACE + MSG (see specs/sender-thoughts.md, "Space thoughts"):
/// SATELLITE for satellites/space stations, SPACE for any other space
/// subject, null for everything else. Whole-word matches only, so "star"
/// doesn't hit "start" and "sun" doesn't hit "Sunday". English is checked
/// on the original text *before* translating — more reliable than on the
/// machine translation.
/// </summary>
internal static class SpaceTopic
{
    private static readonly Regex SatelliteEn = WordList(
        "satellite", "satellites", "iss", "international space station", "space station",
        "sputnik", "hubble", "tiangong", "skylab", "mir");

    private static readonly Regex SpaceEn = WordList(
        "space", "spacecraft", "spaceship", "planet", "planets", "moon", "moons", "lunar", "mars", "martian",
        "jupiter", "saturn", "venus", "uranus", "neptune", "pluto", "sun", "solar", "star", "stars",
        "galaxy", "galaxies", "universe", "cosmos", "cosmic", "astronaut", "astronauts", "cosmonaut", "nasa",
        "orbit", "orbits", "orbiting", "comet", "comets", "asteroid", "asteroids", "meteor", "meteors",
        "meteorite", "telescope", "rocket", "rockets", "light-year", "light-years", "black hole", "milky way",
        "nebula", "eclipse", "apollo");

    private static readonly Regex SatellitePt = WordList(
        "satélite", "satélites", "estação espacial", "iss", "sputnik", "hubble", "tiangong", "skylab", "mir");

    private static readonly Regex SpacePt = WordList(
        "espaço", "espacial", "espaciais", "nave", "lua", "lunar", "marte", "planeta", "planetas", "júpiter",
        "saturno", "vênus", "netuno", "urano", "plutão", "sol", "solar", "estrela", "estrelas", "galáxia",
        "galáxias", "universo", "cosmos", "astronauta", "astronautas", "cosmonauta", "nasa", "apollo",
        "órbita", "orbital", "foguete", "foguetes", "cometa", "asteroide", "meteoro", "meteorito",
        "telescópio", "eclipse", "via láctea", "buraco negro", "ano-luz", "anos-luz");

    /// <summary>"SATELLITE", "SPACE" or null for an English text.</summary>
    public static string? ClassifyEnglish(string text) => Classify(text, SatelliteEn, SpaceEn);

    /// <summary>"SATELLITE", "SPACE" or null for a Portuguese text.</summary>
    public static string? ClassifyPortuguese(string text) => Classify(text, SatellitePt, SpacePt);

    private static string? Classify(string text, Regex satellite, Regex space)
    {
        if (satellite.IsMatch(text)) return "SATELLITE";
        if (space.IsMatch(text)) return "SPACE";
        return null;
    }

    // \b doesn't treat accented letters as word characters consistently
    // across the edges we need, so the boundary is spelled out: not
    // preceded/followed by a letter or digit.
    private static Regex WordList(params string[] words) =>
        new(@"(?<![\p{L}\p{N}])(" + string.Join("|", words.Select(Regex.Escape)) + @")(?![\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
