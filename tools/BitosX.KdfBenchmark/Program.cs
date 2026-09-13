using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BitosX.Crypto;

// BitosX Argon2id key-derivation benchmark (diagnostic only).
//
// Measures the wall-clock time to derive the key-encryption key using the PRODUCTION
// Argon2id profile (Argon2Parameters.Default). The purpose is to check whether unlock
// latency is appropriate for an interactive desktop password manager on a given machine.
//
// This tool NEVER prints the benchmark password or any derived key material. The password
// below is a fixed, non-secret constant used only to feed the KDF; it is not a real secret.
//
// Usage:  dotnet run -c Release --project tools/BitosX.KdfBenchmark [runs]
//         (Release is strongly recommended; Debug numbers are not representative.)

const string BenchmarkPassword = "benchmark-not-a-real-secret";
const int DefaultRuns = 10;

int runs = DefaultRuns;
if (args.Length > 0 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
{
    runs = parsed;
}

Argon2Parameters p = Argon2Parameters.Default;
p.Validate();

var kdf = new Argon2idKeyDerivation();
byte[] password = Encoding.UTF8.GetBytes(BenchmarkPassword);

CultureInfo ci = CultureInfo.InvariantCulture;

Console.WriteLine("BitosX Argon2id production-profile benchmark");
Console.WriteLine("============================================");
Console.WriteLine();
Console.WriteLine("Environment");
Console.WriteLine($"  OS               : {RuntimeInformation.OSDescription}");
Console.WriteLine($"  OS architecture  : {RuntimeInformation.OSArchitecture}");
Console.WriteLine($"  Process arch     : {RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"  .NET runtime     : {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"  Logical CPUs     : {Environment.ProcessorCount}");
Console.WriteLine($"  Build config     : {BuildConfiguration()}");
long availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
if (availableBytes > 0)
{
    Console.WriteLine($"  Memory available : {(availableBytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", ci)} GiB (GC-reported)");
}

Console.WriteLine();
Console.WriteLine("KDF configuration (production profile — NOT weakened for this benchmark)");
Console.WriteLine($"  Algorithm        : Argon2id (Konscious, pure-managed)");
Console.WriteLine($"  Memory           : {p.MemoryKiB} KiB ({(p.MemoryKiB / 1024.0).ToString("0.#", ci)} MiB)");
Console.WriteLine($"  Iterations       : {p.Iterations}");
Console.WriteLine($"  Parallelism      : {p.DegreeOfParallelism}");
Console.WriteLine($"  Derived key len  : {p.KeyLengthBytes} bytes");
Console.WriteLine();

// Warm-up (JIT + first-allocation costs), excluded from the statistics.
Console.Write("Warming up... ");
RunOnce(kdf, password, p);
Console.WriteLine("done.");
Console.WriteLine();

var timingsMs = new double[runs];
Console.WriteLine($"Measuring {runs} derivations:");
for (int i = 0; i < runs; i++)
{
    double ms = RunOnce(kdf, password, p);
    timingsMs[i] = ms;
    Console.WriteLine($"  run {(i + 1).ToString(ci).PadLeft(2)} : {ms.ToString("0.0", ci).PadLeft(8)} ms");
}

CryptoBuffers.Wipe(password);

Array.Sort(timingsMs);
double min = timingsMs[0];
double max = timingsMs[^1];
double avg = timingsMs.Average();
double median = Median(timingsMs);

Console.WriteLine();
Console.WriteLine("Results (milliseconds)");
Console.WriteLine($"  minimum : {min.ToString("0.0", ci)}");
Console.WriteLine($"  median  : {median.ToString("0.0", ci)}");
Console.WriteLine($"  average : {avg.ToString("0.0", ci)}");
Console.WriteLine($"  maximum : {max.ToString("0.0", ci)}");
Console.WriteLine();
Console.WriteLine("Interpretation");
Console.WriteLine("  Interactive password-manager unlock is generally comfortable up to a few");
Console.WriteLine("  hundred milliseconds. Well under ~100 ms may indicate the profile is weaker");
Console.WriteLine("  than desired; multiple seconds is likely too slow for interactive unlock.");
Console.WriteLine("  These numbers are for THIS machine only. Do not tune production defaults from");
Console.WriteLine("  a single device; prefer device-aware calibration and versioned migration.");

static double RunOnce(Argon2idKeyDerivation kdf, byte[] password, Argon2Parameters p)
{
    byte[] salt = SecureRandom.GetBytes(16);
    var sw = Stopwatch.StartNew();
    byte[] key = kdf.DeriveKey(password, salt, p);
    sw.Stop();
    // Never print or return key material; wipe the derived key immediately.
    CryptoBuffers.Wipe(key);
    return sw.Elapsed.TotalMilliseconds;
}

static double Median(double[] sorted)
{
    int n = sorted.Length;
    if (n == 0)
    {
        return 0;
    }

    return n % 2 == 1
        ? sorted[n / 2]
        : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2.0;
}

static string BuildConfiguration()
{
#if DEBUG
    return "Debug (WARNING: not representative — use Release)";
#else
    return "Release";
#endif
}
