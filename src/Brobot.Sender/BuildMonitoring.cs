namespace Brobot.Sender;

/// <summary>
/// Shared by every build-detecting monitor (Gradle, Visual Studio, MSBuild)
/// -- Started fires once when a build begins, Successful/Failed once when
/// it ends. A cancelled build raises neither, since nobody asked for a
/// "you cancelled it" message and it's genuinely ambiguous what face that
/// deserves.
/// </summary>
public enum BuildState
{
    Started,
    Successful,
    Failed,
}
