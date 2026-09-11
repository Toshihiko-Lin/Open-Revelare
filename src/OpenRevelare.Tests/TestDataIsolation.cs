using System.Runtime.CompilerServices;
using OpenRevelare.Gui.Services;
using Xunit;

namespace OpenRevelare.Tests;

/// <summary>
/// Points this assembly's per-user state at a throwaway directory, before any test runs.
///
/// <para>
/// WITHOUT THIS, a test run writes into the developer's real photo library. Every test that
/// constructs a <see cref="OpenRevelare.Gui.ViewModels.MainViewModel"/> and opens a roll reaches
/// <c>RegisterRoll</c> → <c>Catalog.Upsert</c> → <c>%APPDATA%\OpenRevelare\catalog.json</c>. The
/// fixture TIFFs live in the system temp folder, so each entry is titled "Temp"; the fixture is
/// deleted in the test's finally block, and the catalog entry survives as a permanent "文件缺失"
/// card. It accumulates: measured on the machine this was found on, 123 of 124 catalog entries
/// were test residue, and the library's first screen was rows of dead cards.
/// </para>
///
/// <para>
/// A MODULE INITIALIZER and not a fixture, because <see cref="Settings.ConfigDir"/> is
/// <c>static readonly</c> — it is resolved the first time anything touches <c>Settings</c>, and an
/// xUnit fixture is not guaranteed to run before that. Module initializers run at assembly load,
/// which is before any test body, so the environment is always set in time.
/// </para>
///
/// <para>
/// The directory is WIPED on entry rather than made unique per run, so repeated runs neither
/// accumulate state nor inherit it. Nothing deletes it afterwards on purpose: a failing test's
/// leftovers are worth being able to look at.
/// </para>
/// </summary>
public static class TestDataIsolation
{
    /// <summary>The throwaway root, under the system temp folder rather than the repo.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "OpenRevelare-tests");

    [ModuleInitializer]
    internal static void Redirect()
    {
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a previous run's file still held open must not fail the suite */ }
        catch (UnauthorizedAccessException) { }

        string config = Path.Combine(Root, "config");
        string data = Path.Combine(Root, "data");
        Directory.CreateDirectory(config);
        Directory.CreateDirectory(data);

        Environment.SetEnvironmentVariable("OPENREVELARE_CONFIG_DIR", config);
        Environment.SetEnvironmentVariable("OPENREVELARE_DATA_DIR", data);
    }
}

public sealed class TestDataIsolationTests
{
    /// <summary>
    /// The guard that makes the isolation a rule rather than a hope: if the redirect ever stops
    /// working — the module initializer removed, the environment names changed, the fields read
    /// before it runs — this fails loudly instead of quietly resuming writes into a real library.
    /// </summary>
    [Fact]
    public void The_suite_never_writes_to_the_real_per_user_directories()
    {
        Assert.StartsWith(TestDataIsolation.Root, Settings.ConfigDir, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TestDataIsolation.Root, Settings.DataDir, StringComparison.OrdinalIgnoreCase);

        // Named explicitly as well as by prefix: a future refactor that changes Root must not be
        // able to make this pass by accidentally pointing both at the production folder.
        string production = OperatingSystem.IsWindows()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRevelare")
            : Path.Combine(
                Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
                "OpenRevelare");
        Assert.NotEqual(production, Settings.ConfigDir, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The behaviour that actually mattered: opening a roll registers a catalog entry, and that
    /// entry must land in the throwaway catalog. Asserting on the file is what ties the redirect
    /// to the thing it was protecting.
    /// </summary>
    [Fact]
    public void The_catalog_a_roll_registers_into_is_the_throwaway_one()
    {
        string catalog = Path.Combine(Settings.ConfigDir, "catalog.json");

        Assert.StartsWith(TestDataIsolation.Root, catalog, StringComparison.OrdinalIgnoreCase);
        Assert.False(
            File.Exists(Path.Combine(
                OperatingSystem.IsWindows()
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "OpenRevelare")
                    : Settings.ConfigDir,
                "catalog.json.test-marker")),
            "no test artefact may appear in the production config directory");
    }
}
