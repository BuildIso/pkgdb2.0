using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PkgDb20;

class Program
{
    private const string CloudUrl = "https://buildiso.github.io/pkgdb2.0/pkg.json";
    private const string RepoOwner = "BuildIso";
    private const string RepoName = "pkgdb2.0";

    private static readonly string LocalDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".pkgdb2.0");

    private static readonly string LocalDbFile = Path.Combine(LocalDir, "pkgdblocal.json");
    private static readonly string LogFile = Path.Combine(LocalDir, "log.txt");
    private static readonly string SettingsFile = Path.Combine(LocalDir, "settings.json");

    static async Task Main(string[] args)
    {
        Directory.CreateDirectory(LocalDir);

        if (args.Length == 0)
        {
            await RunInteractive();
        }
        else
        {
            await RunCommand(args);
        }
    }

    private static async Task RunInteractive()
    {
        Console.WriteLine("pkgdb2.0 interactive mode");
        while (true)
        {
            Console.Write("pkg> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
                continue;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("exit", StringComparison.OrdinalIgnoreCase))
                return;

            await RunCommand(parts);
        }
    }

    private static async Task RunCommand(string[] args)
    {
        var cmd = args[0].ToLowerInvariant();

        switch (cmd)
        {
            case "auth":
                await Auth();
                break;

            case "install":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: install <name>");
                    return;
                }
                await Install(args[1]);
                break;

            case "upgrade":
                if (args.Length == 2 && args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                    await UpgradeAll();
                else if (args.Length == 2)
                    await Upgrade(args[1]);
                else
                    Console.WriteLine("Usage: upgrade <name> | upgrade all");
                break;

            case "del":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: del <name>");
                    return;
                }
                await Delete(args[1]);
                break;

            case "search":
                if (args.Length < 2)
                {
                    Console.WriteLine("Usage: search <name>");
                    return;
                }
                await Search(args[1]);
                break;

            case "list":
                await ListCloud();
                break;

            case "listlocal":
                ListLocal();
                break;

            case "help":
                PrintHelp();
                break;

            case "create":
                if (args.Length < 5)
                {
                    Console.WriteLine("Usage: create <vendor> <app> <version> <url> [type]");
                    return;
                }
                await CreateManifestAndPr(args[1], args[2], args[3], args[4], args.Length >= 6 ? args[5] : "exe");
                break;

            default:
                Console.WriteLine("Unknown command");
                break;
        }
    }

    // ---------------------------
    // AUTH / SETTINGS
    // ---------------------------

    private static async Task Auth()
    {
        Console.Write("GitHub username: ");
        var user = Console.ReadLine()?.Trim() ?? "";

        Console.Write("GitHub token (PAT): ");
        var token = ReadSecret();

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("Invalid username or token");
            return;
        }

        var settings = new Settings
        {
            GitHubUser = user,
            GitHubToken = token
        };

        SaveSettings(settings);
        await Log("Auth configured");
        Console.WriteLine("Authentication saved.");
    }

    private static string ReadSecret()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return sb.ToString();
    }

    private static Settings LoadSettings()
    {
        if (!File.Exists(SettingsFile))
            return new Settings();

        var json = File.ReadAllText(SettingsFile);
        var s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return s ?? new Settings();
    }

    private static void SaveSettings(Settings s)
    {
        var json = JsonSerializer.Serialize(s, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(SettingsFile, json, Encoding.UTF8);
    }

    // ---------------------------
    // INSTALL / UPGRADE / DEL
    // ---------------------------

    private static async Task Install(string name)
    {
        var cloud = await LoadCloud();
        var pkg = cloud.Packages.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pkg == null)
        {
            Console.WriteLine("Package not found in cloud");
            return;
        }

        Console.WriteLine($"Installing {pkg.Name} {pkg.Version}");

        var manifest = await LoadManifest(pkg.Manifest);
        if (manifest == null)
        {
            Console.WriteLine("Failed to load manifest");
            return;
        }

        var installer = manifest.Installers.Count > 0 ? manifest.Installers[0] : null;
        if (installer == null)
        {
            Console.WriteLine("No installer defined in manifest");
            return;
        }

        var tempFile = Path.Combine(Path.GetTempPath(), $"{pkg.Name}_{pkg.Version}.bin");
        Console.WriteLine($"Downloading installer: {installer.InstallerUrl}");

        using (var http = new HttpClient())
        {
            var data = await http.GetByteArrayAsync(installer.InstallerUrl);
            await File.WriteAllBytesAsync(tempFile, data);

            var sha = ComputeSha256(data);
            Console.WriteLine($"Downloaded SHA256: {sha}");

            if (!string.IsNullOrWhiteSpace(installer.InstallerSha256) &&
                !installer.InstallerSha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("SHA256 mismatch");
                await Log($"SHA256 mismatch for {pkg.Name} {pkg.Version}");
                return;
            }
        }

        Console.WriteLine($"Installer downloaded to: {tempFile}");
        await Log($"Installed {pkg.Name} {pkg.Version}");

        var local = LoadLocalDb();
        var existing = local.Installed.Find(p => p.Name.Equals(pkg.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Version = pkg.Version;
        }
        else
        {
            local.Installed.Add(new LocalPackage { Name = pkg.Name, Version = pkg.Version });
        }
        SaveLocalDb(local);

        Console.WriteLine("You can run the installer manually if needed.");
    }

    private static async Task Upgrade(string name)
    {
        var local = LoadLocalDb();
        var installed = local.Installed.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (installed == null)
        {
            Console.WriteLine("Package not installed locally");
            return;
        }

        var cloud = await LoadCloud();
        var pkg = cloud.Packages.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pkg == null)
        {
            Console.WriteLine("Package not found in cloud");
            return;
        }

        if (pkg.Version == installed.Version)
        {
            Console.WriteLine("Already up to date");
            return;
        }

        Console.WriteLine($"Upgrading {name} from {installed.Version} to {pkg.Version}");
        await Install(name);
    }

    private static async Task UpgradeAll()
    {
        var local = LoadLocalDb();
        if (local.Installed.Count == 0)
        {
            Console.WriteLine("No local packages");
            return;
        }

        foreach (var p in local.Installed)
        {
            Console.WriteLine($"Checking {p.Name}");
            await Upgrade(p.Name);
        }
    }

    private static async Task Delete(string name)
    {
        var local = LoadLocalDb();
        var installed = local.Installed.Find(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (installed == null)
        {
            Console.WriteLine("Package not installed");
            return;
        }

        local.Installed.Remove(installed);
        SaveLocalDb(local);
        await Log($"Deleted {name}");
        Console.WriteLine($"Removed {name} from local db");
    }

    // ---------------------------
    // SEARCH / LIST / HELP
    // ---------------------------

    private static async Task Search(string name)
    {
        var cloud = await LoadCloud();
        var matches = cloud.Packages.FindAll(p =>
            p.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            p.Vendor.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (matches.Count == 0)
        {
            Console.WriteLine("No matches");
            return;
        }

        foreach (var p in matches)
            Console.WriteLine($"{p.Name} {p.Version} ({p.Vendor})");
    }

    private static async Task ListCloud()
    {
        var cloud = await LoadCloud();
        foreach (var p in cloud.Packages)
            Console.WriteLine($"{p.Name} {p.Version} ({p.Vendor})");
    }

    private static void ListLocal()
    {
        var local = LoadLocalDb();
        if (local.Installed.Count == 0)
        {
            Console.WriteLine("No local packages");
            return;
        }

        foreach (var p in local.Installed)
            Console.WriteLine($"{p.Name} {p.Version}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("auth");
        Console.WriteLine("install <name>");
        Console.WriteLine("upgrade <name>");
        Console.WriteLine("upgrade all");
        Console.WriteLine("del <name>");
        Console.WriteLine("search <name>");
        Console.WriteLine("list");
        Console.WriteLine("listlocal");
        Console.WriteLine("create <vendor> <app> <version> <url> [type]");
        Console.WriteLine("help");
        Console.WriteLine("exit");
    }

    // ---------------------------
    // CREATE + MANIFEST + PR
    // ---------------------------

    private static async Task CreateManifestAndPr(string vendor, string app, string version, string url, string type)
    {
        await CreateManifest(vendor, app, version, url, type);
        await CreatePullRequestWorkflow(vendor, app, version);
    }

    private static async Task CreateManifest(string vendor, string app, string version, string url, string type)
    {
        var manifest = new Manifest
        {
            Id = $"{vendor}.{app}",
            Version = version,
            Name = app,
            Publisher = vendor,
            InstallerType = type,
            Installers = new List<Installer>
            {
                new Installer
                {
                    Architecture = "x64",
                    InstallerUrl = url,
                    InstallerSha256 = "<hash>"
                }
            }
        };

        var path = Path.Combine("packages", vendor, app, version);
        Directory.CreateDirectory(path);

        var serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var yaml = serializer.Serialize(manifest);
        File.WriteAllText(Path.Combine(path, "manifest.yaml"), yaml, Encoding.UTF8);

        await Log($"Created manifest for {vendor}.{app} {version}");
        Console.WriteLine("Manifest created locally.");
    }

    private static async Task CreatePullRequestWorkflow(string vendor, string app, string version)
    {
        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.GitHubUser) || string.IsNullOrWhiteSpace(settings.GitHubToken))
        {
            Console.WriteLine("Run 'auth' first.");
            return;
        }

        var branchName = $"{vendor}.{app}.{version}".Replace(" ", "").Replace("/", "-");
        var tempDir = Path.Combine(Path.GetTempPath(), "pkgdb2.0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);


        if (Directory.Exists(tempDir))
            Directory.Delete(tempDir, true);

        Directory.CreateDirectory(tempDir);

        Console.WriteLine("Cloning fork...");
        var cloneUrl = $"https://github.com/{settings.GitHubUser}/{RepoName}.git";

        if (!await RunGit($"clone {cloneUrl} .", tempDir))
        {
            Console.WriteLine("Failed to clone fork.");
            return;
        }

        // 🔥 Maintenant on peut changer l’origin
        await RunGit(
            $"remote set-url origin https://{settings.GitHubUser}:{settings.GitHubToken}@github.com/{settings.GitHubUser}/{RepoName}.git",
            tempDir
        );


        Console.WriteLine("Creating branch...");
        if (!await RunGit($"checkout -b {branchName}", tempDir))
        {
            Console.WriteLine("Failed to create branch.");
            return;
        }

        Console.WriteLine("Copying manifest...");
        CopyManifestToRepo(vendor, app, version, tempDir);

        Console.WriteLine("Committing...");
        await RunGit("add .", tempDir);
        await RunGit($"commit -m \"Add {vendor}.{app} version {version}\"", tempDir);

        Console.WriteLine("Pushing...");
        var pushUrl = $"https://{settings.GitHubUser}:{settings.GitHubToken}@github.com/{settings.GitHubUser}/{RepoName}.git";
        if (!await RunGit($"push {pushUrl} {branchName}", tempDir))
        {
            Console.WriteLine("Failed to push branch.");
            return;
        }

        Console.WriteLine("Creating Pull Request...");
        var prUrl = await CreatePullRequest(settings.GitHubUser, branchName, vendor, app, version);

        if (prUrl != null)
            Console.WriteLine($"Pull Request created:\n{prUrl}");
        else
            Console.WriteLine("Failed to create PR.");

        Console.WriteLine("Cleaning up...");
        Cleanup(tempDir);
    }

    private static void CopyManifestToRepo(string vendor, string app, string version, string repoPath)
    {
        var srcPath = Path.Combine("packages", vendor, app, version, "manifest.yaml");
        var destPath = Path.Combine(repoPath, "packages", vendor, app, version);

        Directory.CreateDirectory(destPath);
        File.Copy(srcPath, Path.Combine(destPath, "manifest.yaml"), overwrite: true);
    }

    private static async Task<bool> RunGit(string arguments, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null) return false;

        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();

        await p.WaitForExitAsync();

        if (!string.IsNullOrWhiteSpace(output))
            Console.WriteLine(output);

        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine(error);

        return p.ExitCode == 0;
    }

    private static async Task<string?> CreatePullRequest(string user, string branch, string vendor, string app, string version)
    {
        var settings = LoadSettings();
        using var client = new HttpClient();

        client.DefaultRequestHeaders.UserAgent.ParseAdd("pkgdb2.0");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("token", settings.GitHubToken);

        var body = new PullRequestRequest
        {
            Title = $"Add {vendor}.{app} version {version}",
            Head = $"{user}:{branch}",
            BaseBranch = "main"
        };

        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync($"https://api.github.com/repos/{RepoOwner}/{RepoName}/pulls", content);

        if (!response.IsSuccessStatusCode)
            return null;

        var respJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);

        return doc.RootElement.GetProperty("html_url").GetString();
    }

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                // On tente une suppression "soft"
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); }
                    catch { }
                }

                try
                {
                    Directory.Delete(path, true);
                }
                catch
                {
                    // Git garde parfois des fichiers pack ouverts → on ignore
                }
            }
        }
        catch
        {
            // On ignore tout
        }
    }


    // ---------------------------
    // CLOUD / LOCAL / UTILS
    // ---------------------------

    private static async Task<CloudDb> LoadCloud()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync(CloudUrl);
        var db = JsonSerializer.Deserialize<CloudDb>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return db ?? new CloudDb();
    }

    private static async Task<Manifest?> LoadManifest(string url)
    {
        using var http = new HttpClient();
        var yaml = await http.GetStringAsync(url);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        return deserializer.Deserialize<Manifest>(yaml);
    }

    private static LocalDb LoadLocalDb()
    {
        if (!File.Exists(LocalDbFile))
            return new LocalDb();

        var json = File.ReadAllText(LocalDbFile);
        var db = JsonSerializer.Deserialize<LocalDb>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return db ?? new LocalDb();
    }

    private static void SaveLocalDb(LocalDb db)
    {
        var json = JsonSerializer.Serialize(db, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(LocalDbFile, json, Encoding.UTF8);
    }

    private static async Task Log(string message)
    {
        var line = $"{DateTime.UtcNow:O} {message}";
        await File.AppendAllTextAsync(LogFile, line + Environment.NewLine, Encoding.UTF8);
    }

    private static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(data);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
    }
}

// ---------------------------
// DATA CLASSES
// ---------------------------

class Settings
{
    public string GitHubUser { get; set; } = "";
    public string GitHubToken { get; set; } = "";
}

class CloudDb
{
    public List<CloudPackage> Packages { get; set; } = new();
}

class CloudPackage
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string Version { get; set; } = "";
    public string Manifest { get; set; } = "";
}

class LocalDb
{
    public List<LocalPackage> Installed { get; set; } = new();
}

class LocalPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

class Manifest
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public List<Installer> Installers { get; set; } = new();
}

class Installer
{
    public string Architecture { get; set; } = "";
    public string InstallerUrl { get; set; } = "";
    public string InstallerSha256 { get; set; } = "";
}

class PullRequestRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("head")]
    public string Head { get; set; } = "";

    [JsonPropertyName("base")]
    public string BaseBranch { get; set; } = "";
}
