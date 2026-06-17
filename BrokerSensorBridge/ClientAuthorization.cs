using Microsoft.Win32.SafeHandles;

namespace BrokerSensorBridge;

/// <summary>Outcome of an authorization check, with the resolved peer identity for auditing.</summary>
/// <param name="Identity">Stable key for the peer across reconnects (image path, or "unknown"),
/// used to key rate limiting and the per-identity session cap so a reconnect loop can't reset them.</param>
internal readonly record struct AuthDecision(bool Allowed, string Who, string How, string Identity);

/*---------------------------------------------------------------------------*\
| ClientAuthorization                                                        |
|                                                                            |
|   Decides whether a connecting pipe client may proceed, from its resolved   |
|   peer-process identity (PeerIdentity) and code signature (PeerSignature).  |
|   With the HMAC handshake removed, this IS the authentication gate — the    |
|   OS pipe DACL keeps out other users, and this keeps out unauthorized       |
|   same-user binaries before any data is served.                             |
|                                                                            |
|   A peer is authorized if it satisfies EITHER policy:                       |
|     • its image path is on AllowedClientPaths       (for self-built,        |
|                                                      unsigned binaries), or  |
|     • its Authenticode signer thumbprint is on AllowedClientSigners         |
|                                                      (the stronger pin —     |
|                                                      survives the binary     |
|                                                      moving on disk).        |
|                                                                            |
|   Enforcement is opt-in so it never silently breaks an existing setup:      |
|     RequireAuthorizedClient = false (default) -> audit only; every          |
|         connection is logged with its pid/image/signer but allowed.         |
|     RequireAuthorizedClient = true            -> only peers matching a       |
|         path or signer proceed; others are dropped before anything is sent. |
\*---------------------------------------------------------------------------*/
internal sealed class ClientAuthorization
{
    private readonly bool _require;
    private readonly string[] _allowedPaths;
    private readonly string[] _allowedSigners;   // normalized thumbprints (uppercase hex, no separators) — SHA-1 (40) or SHA-256 (64)
    private readonly Action<string> _log;

    public ClientAuthorization(bool requireAuthorizedClient, string[]? allowedClientPaths,
                               string[]? allowedClientSigners, Action<string> log)
    {
        _require = requireAuthorizedClient;
        _allowedPaths = NormalizePaths(allowedClientPaths);
        _allowedSigners = NormalizeThumbprints(allowedClientSigners);
        _log = log;
    }

    private static string[] NormalizePaths(string[]? paths)
    {
        if (paths == null) return Array.Empty<string>();
        var result = new List<string>(paths.Length);
        foreach (string p in paths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try { result.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(p))); }
            catch { /* skip malformed allowlist entries */ }
        }
        return result.ToArray();
    }

    private static string[] NormalizeThumbprints(string[]? thumbprints)
    {
        if (thumbprints == null) return Array.Empty<string>();
        var result = new List<string>(thumbprints.Length);
        foreach (string t in thumbprints)
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            // certmgr / signtool render thumbprints with spaces and mixed case; canonicalize.
            string norm = t.Replace(" ", "").Replace(":", "").Replace("-", "").ToUpperInvariant();
            if (norm.Length > 0) result.Add(norm);
        }
        return result.ToArray();
    }

    /// <summary>Resolves the peer and decides whether it may proceed, returning identity for auditing.</summary>
    public AuthDecision Authorize(SafePipeHandle pipe)
    {
        PeerIdentity.TryGetClient(pipe, out uint pid, out string? image);

        bool pathMatch = image != null &&
                         _allowedPaths.Any(p => string.Equals(p, image, StringComparison.OrdinalIgnoreCase));

        bool signerMatch = false;
        string signerInfo = "";
        if (_allowedSigners.Length > 0 && image != null)
        {
            if (PeerSignature.TryGetSigner(image, out string? thumb, out string? thumb256, out string? subject, out bool chainTrusted))
            {
                // Match either the SHA-1 or the SHA-256 thumbprint, so an allowlist may pin on
                // the stronger SHA-256 value (recommended) while existing SHA-1 pins keep working.
                string normSha1   = (thumb ?? "").Replace(" ", "").ToUpperInvariant();
                string normSha256 = (thumb256 ?? "").Replace(" ", "").ToUpperInvariant();
                signerMatch = (normSha1.Length   > 0 && _allowedSigners.Contains(normSha1))
                           || (normSha256.Length > 0 && _allowedSigners.Contains(normSha256));
                signerInfo = $" signer={subject} sha1={thumb} sha256={thumb256} chainTrusted={chainTrusted}";
            }
            else
            {
                signerInfo = " signer=<unsigned/invalid>";
            }
        }

        bool match = pathMatch || signerMatch;
        string how = match ? (pathMatch ? "path" : "signer") : "none";
        string who = $"pid={pid} image={image ?? "<unknown>"}{signerInfo}";
        // Stable across reconnects (no pid): the image path, lowercased. Used to key
        // rate limiting + the per-identity session cap so reconnecting doesn't reset them.
        string identity = (image ?? "unknown").ToLowerInvariant();

        // Audit-only when enforcement is off; otherwise a non-match is rejected.
        bool allowed = !_require || match;

        if (!_require)
            _log($"Control client connected ({who}); enforcement OFF (audit only) [match={how}].");
        else if (match)
            _log($"Control client authorized via {how} ({who}).");
        else
            _log($"Control client REJECTED ({who}); no path/signer match.");

        return new AuthDecision(allowed, who, how, identity);
    }
}
