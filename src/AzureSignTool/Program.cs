﻿#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using AzureSign.Core;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using RSAKeyVaultProvider;
using XenoAtom.CommandLine;

using static AzureSignTool.HRESULT;

namespace AzureSignTool
{
    public class Program
    {
        public static Task<int> Main(string[] args)
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Azure Sign Tool is only supported on Windows.");
                return Task.FromResult(E_PLATFORMNOTSUPPORTED);
            }

            var app = new CommandApp("azuresigntool")
            {
                new VersionOption(version: GetVersion(), prototype: "version"),
                new HelpOption(),
                new SignCommand(),
                new AboutCommand(),
            };

            return app.RunAsync(args).AsTask();

            static string GetVersion()
            {
                Assembly assembly = typeof(Program).Assembly;
                string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

                if (string.IsNullOrEmpty(version))
                {
                    version = assembly.GetName().Version?.ToString();
                }

                return version ?? "0.0.0";
            }
        }
    }

    internal sealed class SignCommand : Command
    {
        private HashSet<string>? _allFiles;
        private List<string> Files { get; set; } = [];

        internal string? KeyVaultUrl { get; set; }
        internal string? KeyVaultClientId { get; set; }
        internal string? KeyVaultClientSecret { get; set; }
        internal string? KeyVaultTenantId { get; set; }
        internal string? KeyVaultCertificate { get; set; }
        internal string? KeyVaultAccessToken { get; set; }
        internal bool UseManagedIdentity { get; set; }
        internal string? SignDescription { get; set; }
        internal string? SignDescriptionUrl { get; set; }
        internal string? Rfc3161TimestampUrl { get; set; }
        internal string? TimestampDigestAlgorithm { get; set; } = "SHA256";
        internal string? FileDigestAlgorithm { get; set; } = "SHA256";
        internal string? AuthenticodeTimestampUrl { get; set; }
        internal List<string> AdditionalCertificates { get; } = [];
        internal bool Verbose { get; set; }
        internal bool Quiet { get; set; }
        internal bool PageHashing { get; set; }
        internal bool NoPageHashing { get; set; }
        internal bool ContinueOnError { get; set; }
        internal string? InputFileList { get; set; }
        internal int? MaxDegreeOfParallelism { get; set; }
        internal bool Colors { get; set; }
        internal bool SkipSignedFiles { get; set; }
        internal bool AppendSignature { get; set; }
        internal string? AzureAuthority { get; set; }

        internal HashSet<string> AllFiles
        {
            get
            {
                if (_allFiles is null)
                {
                    _allFiles = [];
                    Matcher matcher = new();

                    foreach (string file in Files)
                    {
                        Add(_allFiles, matcher, file);
                    }

                    if (!string.IsNullOrWhiteSpace(InputFileList))
                    {
                        foreach(string line in File.ReadLines(InputFileList))
                        {
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                continue;
                            }

                            Add(_allFiles, matcher, line);
                        }
                    }

                    PatternMatchingResult results = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(".")));

                    if (results.HasMatches)
                    {
                        foreach (var result in results.Files)
                        {
                            _allFiles.Add(result.Path);
                        }
                    }
                }

                return _allFiles;

                static void Add(HashSet<string> collection, Matcher matcher, string item)
                {
                    // We require explicit glob pattern wildcards in order to treat it as a glob. e.g.
                    // dir/ will not be treated as a directory. It must be explicitly dir/*.exe or dir/**/*.exe, for example.
                    if (item.Contains('*'))
                    {
                        matcher.AddInclude(item);
                    }
                    else
                    {
                        collection.Add(item);
                    }
                }
            }
        }

        public SignCommand() : base("sign", "Sign a file.", null)
        {
            this.Add(new HelpOption());
            this.Add("kvu|azure-key-vault-url=", "The {URL} to an Azure Key Vault.", v => KeyVaultUrl = v);
            this.Add("kvi|azure-key-vault-client-id=", "The Client {ID} to authenticate to the Azure Key Vault.", v => KeyVaultClientId = v);
            this.Add("kvs|azure-key-vault-client-secret=", "The Client Secret to authenticate to the Azure Key Vault.", v => KeyVaultClientSecret = v);
            this.Add("kvt|azure-key-vault-tenant-id=", "The Tenant Id to authenticate to the Azure Key Vault.", v => KeyVaultTenantId = v);
            this.Add("kvc|azure-key-vault-certificate=", "The name of the certificate in Azure Key Vault.", v => KeyVaultCertificate = v);
            this.Add("kva|azure-key-vault-accesstoken=", "The Access Token to authenticate to the Azure Key Vault.", v => KeyVaultAccessToken = v);
            this.Add("kvm|azure-key-vault-managed-identity", "Use the current Azure mananaged identity.", v => UseManagedIdentity = v is not null);
            this.Add("d|description=", "Provide a description of the signed content.", v => SignDescription = v);
            this.Add("du|description-url=", "Provide a URL with more information about the signed content.", v => SignDescriptionUrl = v);
            this.Add("tr|timestamp-rfc3161=", "Specifies the RFC 3161 timestamp server's URL. If this option (or -t) is not specified, the signed file will not be timestamped.", v => Rfc3161TimestampUrl = v);
            this.Add("td|timestamp-digest=", "Used with the -tr switch to request a digest algorithm used by the RFC 3161 timestamp server.", v => TimestampDigestAlgorithm = v);
            this.Add("fd|file-digest=", "The digest algorithm to hash the file with.", v => FileDigestAlgorithm = v);
            this.Add("t|timestamp-authenticode=", "Specify the legacy timestamp server's URL. This option is generally not recommended. Use the --timestamp-rfc3161 option instead.", v => AuthenticodeTimestampUrl = v);
            this.Add("ac|additional-certificates=", "Specify one or more certificates to include in the public certificate chain.", AdditionalCertificates);
            this.Add("v|verbose", "Specify one or more certificates to include in the public certificate chain.", v => Verbose = v is not null);
            this.Add("q|quiet", "Do not print any output to the console.", v => Quiet = v is not null);
            this.Add("ph|page-hashing", "Generate page hashes for executable files if supported.", v => PageHashing = v is not null);
            this.Add("nph|no-page-hashing", "Suppress page hashes for executable files if supported.", v => NoPageHashing = v is not null);
            this.Add("coe|continue-on-error", "Continue signing multiple files if an error occurs.", v => ContinueOnError = v is not null);
            this.Add("ifl|input-file-list=", "A path to a file that contains a list of files, one per line, to sign.", v => InputFileList = v);
            this.Add("mdop|max-degree-of-parallelism=", "The maximum number of concurrent signing operations.", (int v) => MaxDegreeOfParallelism = v);
            this.Add("colors", "Enable color output on the command line.", v => Colors = v is not null);
            this.Add("s|skip-signed", "Skip files that are already signed.", v => SkipSignedFiles = v is not null);
            this.Add("as|append-signature", "Append the signature, has no effect with --skip-signed.", v => AppendSignature = v is not null);
            this.Add("au|azure-authority=", "The Azure Authority for Azure Key Vault.", v => AzureAuthority = v);
            this.Add("<>", "[files]*", Files);
            Action = Run;
        }

        private ValueTask<int> Run(CommandRunContext context, string[] arguments)
        {
            if (ValidateArguments(context))
            {
                return RunSign();
            }
            else
            {
                context.Error.WriteLine();
                context.Error.WriteLine("Use --help for additional information and usage.");
                return ValueTask.FromResult(E_INVALIDARG);
            }
        }

        private async ValueTask<int> RunSign()
        {
            using (var loggerFactory = LoggerFactory.Create(ConfigureLogging))
            {
                var logger = loggerFactory.CreateLogger<SignCommand>();
                X509Certificate2Collection certificates;

                switch (GetAdditionalCertificates(AdditionalCertificates, logger))
                {
                    case ErrorOr<X509Certificate2Collection>.Ok d:
                        certificates = d.Value;
                        break;
                    case ErrorOr<X509Certificate2Collection>.Err err:
                        logger.LogError(err.Error, err.Error.Message);
                        return E_INVALIDARG;
                    default:
                        logger.LogError("Failed to include additional certificates.");
                        return E_INVALIDARG;
                }

                var configuration = new AzureKeyVaultSignConfigurationSet
                {
                    AzureKeyVaultUrl = new Uri(KeyVaultUrl!),
                    AzureKeyVaultCertificateName = KeyVaultCertificate,
                    AzureClientId = KeyVaultClientId,
                    AzureTenantId = KeyVaultTenantId,
                    AzureAccessToken = KeyVaultAccessToken,
                    AzureClientSecret = KeyVaultClientSecret,
                    ManagedIdentity = UseManagedIdentity,
                    AzureAuthority = AzureAuthority,
                };

                TimeStampConfiguration timeStampConfiguration;

                if (Rfc3161TimestampUrl is not null)
                {
                    timeStampConfiguration = new TimeStampConfiguration(Rfc3161TimestampUrl, ParseHashAlgorithm(TimestampDigestAlgorithm), TimeStampType.RFC3161);
                }
                else if (AuthenticodeTimestampUrl is not null)
                {
                    logger.LogWarning("Authenticode timestamps should only be used for compatibility purposes. RFC3161 timestamps should be used.");
                    timeStampConfiguration = new TimeStampConfiguration(AuthenticodeTimestampUrl, default, TimeStampType.Authenticode);
                }
                else
                {
                    logger.LogWarning("Signatures will not be timestamped. Signatures will become invalid when the signing certificate expires.");
                    timeStampConfiguration = TimeStampConfiguration.None;
                }
                bool? performPageHashing = null;
                if (PageHashing)
                {
                    performPageHashing = true;
                }
                if (NoPageHashing)
                {
                    performPageHashing = false;
                }
                bool appendSignature = AppendSignature;
                var configurationDiscoverer = new KeyVaultConfigurationDiscoverer(logger);
                var materializedResult = await configurationDiscoverer.Materialize(configuration);
                AzureKeyVaultMaterializedConfiguration materialized;
                switch (materializedResult)
                {
                    case ErrorOr<AzureKeyVaultMaterializedConfiguration>.Ok ok:
                        materialized = ok.Value;
                        break;
                    default:
                        logger.LogError("Failed to get configuration from Azure Key Vault.");
                        return E_INVALIDARG;
                }
                int failed = 0, succeeded = 0;
                var cancellationSource = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cancellationSource.Cancel();
                    logger.LogInformation("Cancelling signing operations.");
                };
                var options = new ParallelOptions();
                if (MaxDegreeOfParallelism.HasValue)
                {
                    options.MaxDegreeOfParallelism = MaxDegreeOfParallelism.Value;
                }
                logger.LogTrace("Creating context");

                using (var keyVault = RSAFactory.Create(materialized.TokenCredential, materialized.KeyId, materialized.PublicCertificate))
                using (var signer = new AuthenticodeKeyVaultSigner(keyVault, materialized.PublicCertificate, ParseHashAlgorithm(FileDigestAlgorithm), timeStampConfiguration, certificates))
                {
                    Parallel.ForEach(AllFiles, options, () => (succeeded: 0, failed: 0), (filePath, pls, state) =>
                    {
                        if (cancellationSource.IsCancellationRequested)
                        {
                            pls.Stop();
                        }
                        if (pls.IsStopped)
                        {
                            return state;
                        }
                        using (logger.BeginScope("File: {Id}", filePath))
                        {
                            logger.LogInformation("Signing file.");

                            if (SkipSignedFiles && IsSigned(filePath))
                            {
                                logger.LogInformation("Skipping already signed file.");
                                return (state.succeeded + 1, state.failed);
                            }

                            var result = signer.SignFile(filePath, Description, SignDescriptionUrl, performPageHashing, logger, appendSignature);
                            switch (result)
                            {
                                case COR_E_BADIMAGEFORMAT:
                                    logger.LogError("The Publisher Identity in the AppxManifest.xml does not match the subject on the certificate.");
                                    break;
                                case TRUST_E_SUBJECT_FORM_UNKNOWN:
                                    logger.LogError("The file cannot be signed because it is not a recognized file type for signing or it is corrupt.");
                                    break;
                            }

                            if (result == S_OK)
                            {
                                logger.LogInformation("Signing completed successfully.");
                                return (state.succeeded + 1, state.failed);
                            }
                            else
                            {
                                logger.LogError("Signing failed with error {result}.", $"{result:X2}");
                                if (!ContinueOnError || AllFiles.Count == 1)
                                {
                                    logger.LogInformation("Stopping file signing.");
                                    pls.Stop();
                                }

                                return (state.succeeded, state.failed + 1);
                            }
                        }
                    }, result =>
                    {
                        Interlocked.Add(ref failed, result.failed);
                        Interlocked.Add(ref succeeded, result.succeeded);
                    });
                }
                logger.LogInformation("Successful operations: {succeeded}", succeeded);
                logger.LogInformation("Failed operations: {failed}", failed);

                if (failed > 0 && succeeded == 0)
                {
                    return E_ALL_FAILED;
                }
                else if (failed > 0)
                {
                    return S_SOME_SUCCESS;
                }
                else
                {
                    return S_OK;
                }
            }
        }

        private static bool IsSigned(string filePath)
        {
            const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

            try
            {
                using var certificate = X509Certificate.CreateFromSignedFile(filePath);
                using var certificate2 = new X509Certificate2(certificate);

                foreach (X509Extension ext in certificate2.Extensions)
                {
                    if (ext is X509EnhancedKeyUsageExtension eku)
                    {
                        if (eku.EnhancedKeyUsages[CodeSigningOid] is not null)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        private bool ValidateArguments(CommandRunContext context)
        {
            bool valid = true;

            if (KeyVaultUrl is null)
            {
                context.Error.WriteLine("--azure-key-vault-url is required.");
                valid = false;
            }

            if (KeyVaultCertificate is null)
            {
                context.Error.WriteLine("--azure-key-vault-certificate is required.");
                valid = false;
            }

            if (PageHashing && NoPageHashing)
            {
                context.Error.WriteLine("Cannot use '--page-hashing' and '--no-page-hashing' options together.");
                valid = false;
            }

            if (Quiet && Verbose)
            {
                context.Error.WriteLine("Cannot use '--quiet' and '--verbose' options together.");
                valid = false;
            }
            if (!OneTrue(KeyVaultAccessToken is not null, KeyVaultClientId is not null, UseManagedIdentity))
            {
                context.Error.WriteLine("One of '--azure-key-vault-accesstoken', '--azure-key-vault-client-id' or '--azure-key-vault-managed-identity' must be supplied.");
                valid = false;
            }

            if (Rfc3161TimestampUrl is not null && AuthenticodeTimestampUrl is not null)
            {
                context.Error.WriteLine("Cannot use '--timestamp-rfc3161' and '--timestamp-authenticode' options together.");
                valid = false;
            }

            if (KeyVaultClientId is not null && KeyVaultClientSecret is null)
            {
                context.Error.WriteLine("Must supply '--azure-key-vault-client-secret' when using '--azure-key-vault-client-id'.");
                valid = false;
            }

            if (KeyVaultClientId is not null && KeyVaultTenantId is null)
            {
                context.Error.WriteLine("Must supply '--azure-key-vault-tenant-id' when using '--azure-key-vault-client-id'.");
                valid = false;
            }

            if (UseManagedIdentity && (KeyVaultAccessToken is not null || KeyVaultClientId is not null))
            {
                context.Error.WriteLine("Cannot use '--azure-key-vault-managed-identity' and '--azure-key-vault-accesstoken' or '--azure-key-vault-client-id'.");
                valid = false;
            }

            if (AppendSignature && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                context.Error.WriteLine("'--append-signature' requires Windows 11 or later.");
                valid = false;
            }

            if (AppendSignature && AuthenticodeTimestampUrl is not null)
            {
                context.Error.WriteLine("Cannot use '--append-signature' and '--timestamp-authenticode' options together.");
                valid = false;
            }

            if (InputFileList is not null && !File.Exists(InputFileList))
            {
                context.Error.WriteLine($"File '{InputFileList}' does not exist.");
                valid = false;
            }

            valid &= ValidateHashAlgorithm(context, FileDigestAlgorithm, "--file-digest");
            valid &= ValidateHashAlgorithm(context, TimestampDigestAlgorithm, "--timestamp-digest");

            if (MaxDegreeOfParallelism is < -1 or 0)
            {
                context.Error.WriteLine("'--max-degree-of-parallelism' must be a positive interger, or negative one.");
                valid = false;
            }

            if (AzureAuthority is not null && AuthorityHostNames.GetUriForAzureAuthorityIdentifier(AzureAuthority) is null)
            {
                context.Error.WriteLine($"'{AzureAuthority}' is not a valid value for '--azure-authority'. Allowed values are [{string.Join(", ", AuthorityHostNames.Keys)}].");
                valid = false;
            }

            if (AllFiles.Count == 0)
            {
                context.Error.WriteLine("At least one file must be specified to sign.");
                valid = false;
            }
            else
            {
                foreach (string file in AllFiles)
                {
                    if (!File.Exists(file))
                    {
                        context.Error.WriteLine($"File '{file}' does not exist.");
                        valid = false;
                    }
                }
            }

            return valid;
        }

        private static bool ValidateHashAlgorithm(CommandRunContext context, string? input, string optionName)
        {
            if (input is null)
            {
                context.Error.WriteLine($"'{optionName}' is required. Allowed values are [{string.Join(", ", s_hashAlgorithm)}].");
                return false;
            }

            foreach(string a in s_hashAlgorithm)
            {
                if (input.Equals(a, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            context.Error.WriteLine($"'{input}' is not a valid hash algorithm for '{optionName}'. Allowed values are [{string.Join(", ", s_hashAlgorithm)}].");
            return false;
        }

        private void ConfigureLogging(ILoggingBuilder builder)
        {
            builder.AddSimpleConsole(console => {
                console.IncludeScopes = true;
                console.ColorBehavior = Colors ? LoggerColorBehavior.Enabled : LoggerColorBehavior.Disabled;
            });

            builder.SetMinimumLevel(LogLevel);
        }

        private LogLevel LogLevel
        {
            get
            {
                if (Quiet)
                {
                    return LogLevel.Critical;
                }
                else if (Verbose)
                {
                    return LogLevel.Trace;
                }
                else
                {
                    return LogLevel.Information;
                }
            }
        }

        private static ErrorOr<X509Certificate2Collection> GetAdditionalCertificates(IEnumerable<string> paths, ILogger logger)
        {
            var collection = new X509Certificate2Collection();
            try
            {
                foreach (var path in paths)
                {

                    var type = X509Certificate2.GetCertContentType(path);
                    switch (type)
                    {
                        case X509ContentType.Cert:
                        case X509ContentType.Authenticode:
                        case X509ContentType.SerializedCert:
                            var certificate = new X509Certificate2(path);
                            logger.LogTrace("Including additional certificate {thumbprint}.", certificate.Thumbprint);
                            collection.Add(certificate);
                            break;
                        default:
                            return new Exception($"Specified file {path} is not a public valid certificate.");
                    }
                }
            }
            catch (CryptographicException e)
            {
                logger.LogError(e, "An exception occurred while including an additional certificate.");
                return e;
            }

            return collection;
        }

        private static HashAlgorithmName ParseHashAlgorithm(string? hashAlgorithm)
        {
            if ("SHA1".Equals(hashAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                return HashAlgorithmName.SHA1;
            }
            if ("SHA256".Equals(hashAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                return HashAlgorithmName.SHA256;
            }
            if ("SHA384".Equals(hashAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                return HashAlgorithmName.SHA384;
            }
            if ("SHA512".Equals(hashAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                return HashAlgorithmName.SHA512;
            }

            throw new ArgumentException("Invalid hash algorithm", nameof(hashAlgorithm));
        }

        private static bool OneTrue(params bool[] values)
        {
            int count = 0;

            for (int i = 0; i < values.Length && count < 2; i++)
            {
                if (values[i])
                {
                    count++;
                }
            }

            return count == 1;
        }

        private static readonly string[] s_hashAlgorithm = ["SHA1", "SHA256", "SHA384", "SHA512"];
    }
}
