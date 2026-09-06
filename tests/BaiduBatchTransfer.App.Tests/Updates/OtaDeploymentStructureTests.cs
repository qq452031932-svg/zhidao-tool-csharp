using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BaiduBatchTransfer.App.Tests.Updates;

public sealed class OtaDeploymentStructureTests
{
    [Fact]
    public void NginxArtifactIsAnAdminOnlyLoopbackIncludeSnippet()
    {
        var nginx = ReadDeploymentFile("ota.fanfh.cn.admin.nginx.conf");

        Assert.Matches(@"location\s+\^~\s+/admin/\s*\{", nginx);
        Assert.Single(Regex.Matches(nginx, @"\bproxy_pass\b"));
        Assert.Matches(@"proxy_pass\s+http://127\.0\.0\.1:18091\s*;", nginx);
        Assert.Contains("Cache-Control \"no-store", nginx, StringComparison.Ordinal);
        Assert.True(Regex.Matches(nginx, "Cache-Control \\\"no-store", RegexOptions.CultureInvariant).Count >= 2,
            "Both /admin and /admin/ responses must be non-cacheable.");
        Assert.True(Regex.Matches(nginx, @"\$scheme\s*!=\s*https", RegexOptions.CultureInvariant).Count >= 2,
            "The include must fail closed to HTTPS even if an operator includes it in both server blocks.");
        Assert.DoesNotMatch(@"(?m)^\s*(server|server_name|listen|root)\b", nginx);
        Assert.DoesNotContain("/health", nginx, StringComparison.Ordinal);
        Assert.DoesNotContain("acme-challenge", nginx, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/releases/", nginx, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceUsesDedicatedIdentityAndCentOsSevenCompatibleHardening()
    {
        var unit = ReadDeploymentFile("superpan-ota-admin.service");

        Assert.Contains("User=superpan-ota-admin", unit, StringComparison.Ordinal);
        Assert.Contains("Group=superpan-ota-admin", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/opt/superpan-ota-admin/ota-admin serve --config /etc/superpan-ota-admin/config.json", unit, StringComparison.Ordinal);
        Assert.Contains("NoNewPrivileges=true", unit, StringComparison.Ordinal);
        Assert.Contains("PrivateTmp=true", unit, StringComparison.Ordinal);
        Assert.Contains("PrivateDevices=true", unit, StringComparison.Ordinal);
        Assert.Contains("ProtectSystem=full", unit, StringComparison.Ordinal);
        Assert.Contains("ProtectHome=true", unit, StringComparison.Ordinal);
        Assert.Contains("InaccessibleDirectories=/www/wwwroot", unit, StringComparison.Ordinal);
        Assert.Contains("ReadWriteDirectories=/var/lib/superpan-ota-admin", unit, StringComparison.Ordinal);
        Assert.Contains("Requires=var-lib-otaadmin-releases.mount", unit, StringComparison.Ordinal);
        Assert.Contains("ReadWriteDirectories=/var/lib/otaadmin/releases", unit, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadWriteDirectories=/www/wwwroot", unit, StringComparison.Ordinal);
        foreach (var unsupported in new[] { "ReadWritePaths=", "StateDirectory=", "ProtectKernelTunables=", "ProtectControlGroups=", "RestrictNamespaces=", "LockPersonality=", "MemoryDenyWriteExecute=" })
            Assert.DoesNotContain(unsupported, unit, StringComparison.Ordinal);

        var mount = ReadDeploymentFile("var-lib-otaadmin-releases.mount");
        Assert.Contains("What=/www/wwwroot/ota.fanfh.cn/releases/win-x64", mount, StringComparison.Ordinal);
        Assert.Contains("Where=/var/lib/otaadmin/releases", mount, StringComparison.Ordinal);
        Assert.Contains("Options=bind", mount, StringComparison.Ordinal);
        Assert.Contains("Before=superpan-ota-admin.service", mount, StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleConfigIsLoopbackOnlyAndUsesIsolatedProductionPaths()
    {
        using var document = JsonDocument.Parse(ReadDeploymentFile("config.example.json"));
        var root = document.RootElement;

        Assert.Equal("/var/lib/superpan-ota-admin/state.db", root.GetProperty("database_path").GetString());
        Assert.Equal("127.0.0.1:18091", root.GetProperty("listen_address").GetString());
        Assert.Equal("https://ota.fanfh.cn", root.GetProperty("public_origin").GetString());
        Assert.Equal("/var/lib/superpan-ota-admin/staging", root.GetProperty("staging_root").GetString());
        Assert.Equal("/var/lib/otaadmin/releases", root.GetProperty("release_root").GetString());
        Assert.Equal("/etc/superpan-ota-admin/publisher.cer", root.GetProperty("publisher_certificate_path").GetString());
        Assert.Equal("f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff", root.GetProperty("publisher_spki_sha256").GetString());
        Assert.Equal("/etc/superpan-ota-admin/source-hmac.key", root.GetProperty("source_hmac_key_path").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("limits").ValueKind);
    }

    [Fact]
    public void InstallerVerifiesInputsBeforeMutationAndInstallsLeastPrivilegeState()
    {
        var install = ReadDeploymentFile("install.sh");
        var firstMutation = install.IndexOf("install -d", StringComparison.Ordinal);

        Assert.True(firstMutation > 0, "Installer must have an explicit mutation boundary.");
        Assert.True(install.IndexOf("verify_sha256 \"$BINARY_SOURCE\"", StringComparison.Ordinal) is var binaryVerify && binaryVerify >= 0 && binaryVerify < firstMutation);
        Assert.True(install.IndexOf("verify_sha256 \"$CERTIFICATE_SOURCE\"", StringComparison.Ordinal) is var certificateVerify && certificateVerify >= 0 && certificateVerify < firstMutation);
        Assert.True(install.IndexOf("verify_sha256 \"$ACTIVE_VHOST\"", StringComparison.Ordinal) is var vhostVerify && vhostVerify >= 0 && vhostVerify < firstMutation);
        Assert.Contains("getent group superpan-ota-admin", install, StringComparison.Ordinal);
        Assert.Contains("useradd --system", install, StringComparison.Ordinal);
        Assert.Contains("openssl rand -hex 32", install, StringComparison.Ordinal);
        Assert.Contains("chown root:superpan-ota-admin \"$CONFIG_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("chmod 0640 \"$CONFIG_TARGET\"", install, StringComparison.Ordinal);
        var nginxGate = install.IndexOf("if ! \"$NGINX_BIN\" -t; then", StringComparison.Ordinal);
        var nginxReload = install.IndexOf("\nsystemctl reload nginx\n", StringComparison.Ordinal);
        Assert.True(nginxGate >= 0 && nginxReload > nginxGate, "The executable nginx test must gate the executable reload.");
        Assert.Contains("init-admin --if-missing --config", install, StringComparison.Ordinal);
        Assert.DoesNotContain("if [ ! -e \"$DATABASE_PATH\" ]", install, StringComparison.Ordinal);
        Assert.Contains("install -d -m 0700 -o \"$SERVICE_USER\" -g \"$SERVICE_GROUP\" \"$DATA_ROOT\"", install, StringComparison.Ordinal);
        Assert.Contains("INITIAL_PASSWORD_FILE", install, StringComparison.Ordinal);
        Assert.Contains(">\"$PASSWORD_TEMP\"", install, StringComparison.Ordinal);
        Assert.Contains("unsafe interrupted initial-password file", install, StringComparison.Ordinal);
        Assert.Contains("unlink \"$PASSWORD_TEMP\"", install, StringComparison.Ordinal);
        Assert.Contains("Rollback", install, StringComparison.Ordinal);
        Assert.Contains("require_value \"$1\" \"${2:-}\"", install, StringComparison.Ordinal);
        Assert.Contains("COMPILED_PUBLISHER_SPKI_SHA256=\"f905188d79bb427c1a6eacc4bc9741d68a407228f643f6a50bfd791211da6cff\"", install, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_SPKI_SHA256\" = \"$COMPILED_PUBLISHER_SPKI_SHA256", install, StringComparison.Ordinal);
        var readiness = install.IndexOf("curl --noproxy '*' --fail --silent --show-error --max-time 2 http://127.0.0.1:18091/admin/", StringComparison.Ordinal);
        Assert.True(readiness >= 0 && readiness < nginxReload, "Local readiness must gate Nginx reload.");
        Assert.Contains("systemctl is-active --quiet \"$SERVICE_NAME\"", install, StringComparison.Ordinal);
        Assert.Contains("INSTALL_SUCCEEDED=false", install, StringComparison.Ordinal);
        Assert.Contains("trap report_install_result EXIT", install, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerIsIdempotentAndBacksUpOnlyEnumeratedAdminTargets()
    {
        var install = ReadDeploymentFile("install.sh");

        Assert.Contains("backup_target \"$BINARY_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_target \"$UNIT_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_target \"$MOUNT_UNIT_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_target \"$NGINX_SNIPPET_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_target \"$CONFIG_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_target \"$ACTIVE_VHOST\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_persistent_target \"$HMAC_TARGET\"", install, StringComparison.Ordinal);
        Assert.Contains("backup_persistent_target \"$INITIAL_PASSWORD_FILE\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("backup_persistent_target \"$PASSWORD_TEMP\"", install, StringComparison.Ordinal);
        Assert.Contains("transient-targets.txt", install, StringComparison.Ordinal);
        Assert.DoesNotContain("backup_target \"$HMAC_TARGET\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("backup_target \"$INITIAL_PASSWORD_FILE\"", install, StringComparison.Ordinal);
        Assert.Contains("persistent-new-targets.txt", install, StringComparison.Ordinal);
        Assert.Contains("persistent-existing-targets.txt", install, StringComparison.Ordinal);
        var persistentFunction = install[install.IndexOf("backup_persistent_target()", StringComparison.Ordinal)..install.IndexOf("# Only individually enumerated", StringComparison.Ordinal)];
        Assert.DoesNotContain("present-targets.txt", persistentFunction, StringComparison.Ordinal);
        Assert.Contains("unit-enabled-state.txt", install, StringComparison.Ordinal);
        Assert.Contains("unit-active-state.txt", install, StringComparison.Ordinal);
        Assert.Contains("systemctl is-enabled", install, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active", install, StringComparison.Ordinal);
        Assert.Contains("systemctl restart \"$MOUNT_UNIT_NAME\"", install, StringComparison.Ordinal);
        Assert.Contains("mountpoint -q \"$RELEASE_VIEW_ROOT\"", install, StringComparison.Ordinal);
        Assert.Contains("stat -Lc '%d:%i'", install, StringComparison.Ordinal);
        Assert.Contains("root:root", install, StringComparison.Ordinal);
        Assert.Contains("release bind target metadata is unsafe", install, StringComparison.Ordinal);
        Assert.DoesNotContain("systemctl daemon-reload && systemctl start ${SERVICE_NAME}", install, StringComparison.Ordinal);
        Assert.Contains("sha256sum -- \"$ACTIVE_VHOST\"", install, StringComparison.Ordinal);
        Assert.Contains("mktemp -d \"${BACKUP_ROOT}/${TIMESTAMP}.XXXXXX\"", install, StringComparison.Ordinal);
        Assert.Contains("release-directory-metadata.txt", install, StringComparison.Ordinal);
        Assert.Contains("Unexpected release root", install, StringComparison.Ordinal);
        Assert.Contains("if [ ! -s \"$HMAC_TARGET\" ]", install, StringComparison.Ordinal);
        Assert.DoesNotContain("--backup-root", install, StringComparison.Ordinal);
        Assert.Contains("validate_backup_root", install, StringComparison.Ordinal);
        Assert.Contains("chown \"$SERVICE_USER\":\"$SERVICE_GROUP\" \"$RELEASE_ROOT\" \"${RELEASE_ROOT}/generations\"", install, StringComparison.Ordinal);
        Assert.Contains("chmod 0755 \"$RELEASE_ROOT\" \"${RELEASE_ROOT}/generations\"", install, StringComparison.Ordinal);
        Assert.DoesNotContain("chmod 2775", install, StringComparison.Ordinal);
        Assert.DoesNotContain("cp -a /www", install, StringComparison.Ordinal);
        Assert.DoesNotContain("tar ", install, StringComparison.Ordinal);
    }

    [Fact]
    public void VerificationChecksAdminAndStaticIndependenceWithoutChangingBaselines()
    {
        var verify = ReadDeploymentFile("verify.sh");

        Assert.Contains("127.0.0.1:18091", verify, StringComparison.Ordinal);
        Assert.Contains("systemctl is-active --quiet superpan-ota-admin.service", verify, StringComparison.Ordinal);
        Assert.Contains("https://ota.fanfh.cn/admin/", verify, StringComparison.Ordinal);
        Assert.Contains("http://ota.fanfh.cn/admin/", verify, StringComparison.Ordinal);
        Assert.Contains("Location", verify, StringComparison.Ordinal);
        Assert.Contains("Cache-Control", verify, StringComparison.Ordinal);
        Assert.Contains("no-store", verify, StringComparison.Ordinal);
        Assert.Contains("upload-manifest.sha256", verify, StringComparison.Ordinal);
        Assert.Contains("upload-manifest.sha256.sig", verify, StringComparison.Ordinal);
        Assert.Contains("no-cache", verify, StringComparison.Ordinal);
        Assert.Contains("systemctl stop superpan-ota-admin.service", verify, StringComparison.Ordinal);
        Assert.Contains("systemctl start superpan-ota-admin.service", verify, StringComparison.Ordinal);
        Assert.Contains("STABLE_PROBE_URL", verify, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_CERT_SHA256", verify, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_HEALTH_SHA256", verify, StringComparison.Ordinal);
        Assert.Contains("EXPECTED_STABLE_SHA256", verify, StringComparison.Ordinal);
        Assert.Contains("-verify_hostname ota.fanfh.cn", verify, StringComparison.Ordinal);
        Assert.Contains("-connect 127.0.0.1:443", verify, StringComparison.Ordinal);
        Assert.Contains("--resolve 'ota.fanfh.cn:443:127.0.0.1'", verify, StringComparison.Ordinal);
        Assert.Contains("--resolve 'ota.fanfh.cn:80:127.0.0.1'", verify, StringComparison.Ordinal);
        Assert.Contains("--max-time", verify, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*rm\s", verify);
        Assert.DoesNotMatch(@"(?m)^\s*mv\s", verify);
        Assert.DoesNotContain("systemctl start \"$SERVICE_NAME\" || true", verify, StringComparison.Ordinal);
        Assert.True(verify.IndexOf("SERVICE_STOPPED=true", StringComparison.Ordinal) <
                    verify.IndexOf("systemctl stop superpan-ota-admin.service", StringComparison.Ordinal),
            "The recovery trap must be armed before stop is attempted.");
    }

    [Fact]
    public void DeploymentArtifactsNeverReferenceTheUnrelatedSite()
    {
        foreach (var relativePath in new[]
        {
            "superpan-ota-admin.service", "var-lib-otaadmin-releases.mount", "ota.fanfh.cn.admin.nginx.conf", "config.example.json", "install.sh", "verify.sh"
        })
            Assert.DoesNotContain("f.fanfh.cn", ReadDeploymentFile(relativePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadDeploymentFile(string name) =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "ota-admin", name));

    private static string FindRepositoryRoot([CallerFilePath] string sourceFilePath = "")
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "BaiduBatchTransfer.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
