using System.Security.Cryptography;
using System.Text.Json;

namespace PageToMovie.Engine.Collaboration;

/// <summary>
/// Project ACL: owner / editors / viewers + pending email/username invites.
/// Persisted at {projectDir}/project-acl.json as <see cref="ProjectAclDocument"/>.
/// </summary>
public sealed class ProjectAclService : IProjectAclService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _projectsRoot;
    private readonly IProjectUserDirectory? _users;
    private readonly IProjectInviteMailer? _email;

    public ProjectAclService(string projectsRoot, IProjectUserDirectory? users = null, IProjectInviteMailer? email = null)
    {
        _projectsRoot = projectsRoot ?? throw new ArgumentNullException(nameof(projectsRoot));
        _users = users;
        _email = email;
    }

    public async Task<ProjectAclDocument> GetOrCreateAclAsync(string projectId, string ownerUserId, CancellationToken ct = default)
    {
        var existing = await LoadAsync(projectId, ct);
        if (existing is not null) return existing;
        var doc = new ProjectAclDocument { OwnerUserId = Norm(ownerUserId), Rev = 1 };
        await SaveAclAsync(projectId, doc, ct);
        return doc;
    }

    public async Task<ProjectAclDocument?> GetAclAsync(string projectId, CancellationToken ct = default) =>
        await LoadAsync(projectId, ct);

    public async Task SaveAclAsync(string projectId, ProjectAclDocument acl, CancellationToken ct = default)
    {
        acl.UpdatedAt = DateTimeOffset.UtcNow;
        var dir = Path.Combine(_projectsRoot, projectId);
        Directory.CreateDirectory(dir);
        var path = AclPath(projectId);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, acl, JsonOpts, ct);
        File.Copy(tmp, path, overwrite: true);
        try { File.Delete(tmp); } catch { }
    }

    public async Task<ProjectAccessLevel> GetAccessLevelAsync(string projectId, string userId, CancellationToken ct = default)
    {
        userId = Norm(userId);
        if (string.IsNullOrEmpty(userId)) return ProjectAccessLevel.None;
        var acl = await LoadAsync(projectId, ct);
        if (acl is null)
            return string.Equals(InferOwner(projectId), userId, StringComparison.OrdinalIgnoreCase)
                ? ProjectAccessLevel.Owner : ProjectAccessLevel.None;
        if (string.Equals(acl.OwnerUserId, userId, StringComparison.OrdinalIgnoreCase)) return ProjectAccessLevel.Owner;
        if (acl.Editors.Contains(userId, StringComparer.OrdinalIgnoreCase)) return ProjectAccessLevel.Editor;
        if (acl.Viewers.Contains(userId, StringComparer.OrdinalIgnoreCase)) return ProjectAccessLevel.Viewer;
        return ProjectAccessLevel.None;
    }

    public async Task<bool> CanAccessAsync(string projectId, string userId, ProjectAccessLevel minimum, CancellationToken ct = default) =>
        await GetAccessLevelAsync(projectId, userId, ct) >= minimum;

    public async Task InviteEditorAsync(string projectId, string ownerUserId, string editorUserId, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, ownerUserId, ct);
        EnsureOwner(acl, ownerUserId);
        editorUserId = Norm(editorUserId);
        if (string.IsNullOrEmpty(editorUserId)) throw new InvalidOperationException("editorUserId required.");
        if (!acl.Editors.Contains(editorUserId, StringComparer.OrdinalIgnoreCase))
            acl.Editors.Add(editorUserId);
        acl.Viewers.RemoveAll(v => string.Equals(v, editorUserId, StringComparison.OrdinalIgnoreCase));
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
    }

    public async Task RemoveEditorAsync(string projectId, string ownerUserId, string editorUserId, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, ownerUserId, ct);
        EnsureOwner(acl, ownerUserId);
        editorUserId = Norm(editorUserId);
        acl.Editors.RemoveAll(e => string.Equals(e, editorUserId, StringComparison.OrdinalIgnoreCase));
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
    }

    public async Task InviteViewerAsync(string projectId, string ownerUserId, string viewerUserId, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, ownerUserId, ct);
        EnsureOwner(acl, ownerUserId);
        viewerUserId = Norm(viewerUserId);
        if (string.IsNullOrEmpty(viewerUserId)) throw new InvalidOperationException("viewerUserId required.");
        if (acl.Editors.Contains(viewerUserId, StringComparer.OrdinalIgnoreCase))
            return; // already has stronger access
        if (!acl.Viewers.Contains(viewerUserId, StringComparer.OrdinalIgnoreCase))
            acl.Viewers.Add(viewerUserId);
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
    }

    public async Task RemoveViewerAsync(string projectId, string ownerUserId, string viewerUserId, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, ownerUserId, ct);
        EnsureOwner(acl, ownerUserId);
        viewerUserId = Norm(viewerUserId);
        acl.Viewers.RemoveAll(v => string.Equals(v, viewerUserId, StringComparison.OrdinalIgnoreCase));
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
    }

    // ---- Pending email/username invite flow ----

    public async Task<InviteResult> InviteByUsernameAsync(
        string projectId,
        string usernameOrEmail,
        string role,
        string callerUserId,
        string? publicBaseUrl = null,
        CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, callerUserId, ct);
        EnsureOwner(acl, callerUserId);
        usernameOrEmail = Norm(usernameOrEmail);
        if (string.IsNullOrEmpty(usernameOrEmail))
            return new InviteResult { Ok = false, Error = "Username or email required." };

        role = string.Equals(role, "viewer", StringComparison.OrdinalIgnoreCase) ? "viewer" : "editor";

        ProjectUserInfo? user = null;
        if (_users is not null)
        {
            user = await _users.FindByUsernameAsync(usernameOrEmail, ct)
                   ?? await _users.FindByEmailAsync(usernameOrEmail, ct)
                   ?? await _users.FindByIdAsync(usernameOrEmail, ct);
        }

        if (user is not null && !string.IsNullOrWhiteSpace(user.UserId))
        {
            if (role == "viewer")
                await InviteViewerAsync(projectId, callerUserId, user.UserId, ct);
            else
                await InviteEditorAsync(projectId, callerUserId, user.UserId, ct);

            acl = await GetOrCreateAclAsync(projectId, callerUserId, ct);
            acl.PendingInvites.RemoveAll(i => Matches(i, usernameOrEmail, user.UserId, user.Email));
            acl.Rev++;
            await SaveAclAsync(projectId, acl, ct);
            return new InviteResult
            {
                Ok = true, Status = "granted", UserId = user.UserId, Role = role,
                Message = $"Granted {role} to {user.UserId}."
            };
        }

        // Pending
        acl = await GetOrCreateAclAsync(projectId, callerUserId, ct);
        var existing = acl.PendingInvites.FirstOrDefault(i =>
            string.Equals(i.Username, usernameOrEmail, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Email, usernameOrEmail, StringComparison.OrdinalIgnoreCase));
        var token = existing?.Token ?? CreateToken();
        var isEmail = usernameOrEmail.Contains('@');
        var invite = new PendingInvite
        {
            Username = isEmail ? null : usernameOrEmail,
            Email = isEmail ? usernameOrEmail : existing?.Email,
            Role = role,
            Token = token,
            InvitedBy = callerUserId,
            CreatedUtc = existing?.CreatedUtc ?? DateTimeOffset.UtcNow,
            LastSentUtc = DateTimeOffset.UtcNow
        };
        acl.PendingInvites.RemoveAll(i =>
            string.Equals(i.Username, usernameOrEmail, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Email, usernameOrEmail, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Token, token, StringComparison.Ordinal));
        acl.PendingInvites.Add(invite);
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);

        var acceptUrl = string.IsNullOrWhiteSpace(publicBaseUrl)
            ? $"/invite/{token}"
            : $"{publicBaseUrl.TrimEnd('/')}/invite/{token}";

        var emailed = false;
        if (isEmail && _email is not null)
        {
            var subject = $"You're invited to a PageToMovie project ({projectId})";
            var body =
                $"You've been invited as {role} on project \"{projectId}\".\n\n" +
                $"Accept:\n{acceptUrl}\n\n" +
                "Sign in first (if needed), then open the link while signed in.\n";
            try { await _email.SendAsync(usernameOrEmail, subject, body, ct); emailed = true; }
            catch { /* pending remains */ }
        }

        return new InviteResult
        {
            Ok = true,
            Status = "pending",
            Role = role,
            Token = token,
            InviteLink = acceptUrl,
            EmailSent = emailed,
            Message = emailed
                ? $"Invite email sent to {usernameOrEmail}."
                : isEmail
                    ? $"Pending invite for {usernameOrEmail} (email not sent — check mail config)."
                    : $"Pending invite for '{usernameOrEmail}'. Share the invite link."
        };
    }

    public async Task<InviteResult> ResendInviteAsync(
        string projectId, string usernameOrEmail, string callerUserId,
        string? publicBaseUrl = null, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, callerUserId, ct);
        EnsureOwner(acl, callerUserId);
        var key = Norm(usernameOrEmail);
        var inv = acl.PendingInvites.FirstOrDefault(i =>
            string.Equals(i.Username, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Email, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Token, key, StringComparison.Ordinal));
        if (inv is null)
            return new InviteResult { Ok = false, Error = "No pending invite found." };
        return await InviteByUsernameAsync(projectId, inv.Email ?? inv.Username ?? usernameOrEmail,
            inv.Role, callerUserId, publicBaseUrl, ct);
    }

    public async Task<ProjectAclDocument> RevokeInviteAsync(string projectId, string key, string callerUserId, CancellationToken ct = default)
    {
        var acl = await GetOrCreateAclAsync(projectId, callerUserId, ct);
        EnsureOwner(acl, callerUserId);
        key = Norm(key);
        acl.PendingInvites.RemoveAll(i =>
            string.Equals(i.Username, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Email, key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.Token, key, StringComparison.Ordinal));
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
        return acl;
    }

    public async Task<(string ProjectId, PendingInvite Invite)?> FindInviteByTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !Directory.Exists(_projectsRoot)) return null;
        token = token.Trim();
        foreach (var dir in Directory.EnumerateDirectories(_projectsRoot))
        {
            var projectId = Path.GetFileName(dir);
            if (string.IsNullOrWhiteSpace(projectId) || projectId.StartsWith('.')) continue;
            try
            {
                var acl = await LoadAsync(projectId, ct);
                var inv = acl?.PendingInvites.FirstOrDefault(i => string.Equals(i.Token, token, StringComparison.Ordinal));
                if (inv is not null) return (projectId, inv);
            }
            catch { }
        }
        return null;
    }

    public async Task<(bool Ok, string? ProjectId, string? Error)> AcceptInviteAsync(
        string token, string acceptingUserId, string? acceptingEmail = null, string? acceptingUsername = null,
        CancellationToken ct = default)
    {
        acceptingUserId = Norm(acceptingUserId);
        if (string.IsNullOrEmpty(acceptingUserId))
            return (false, null, "Not signed in.");

        var found = await FindInviteByTokenAsync(token, ct);
        if (found is null) return (false, null, "Invite not found or already used.");

        var (projectId, inv) = found.Value;
        var acl = await LoadAsync(projectId, ct);
        if (acl is null) return (false, null, "Invite not found or already used.");
        if (string.Equals(inv.Role, "viewer", StringComparison.OrdinalIgnoreCase))
        {
            if (!acl.Editors.Contains(acceptingUserId, StringComparer.OrdinalIgnoreCase) &&
                !acl.Viewers.Contains(acceptingUserId, StringComparer.OrdinalIgnoreCase))
                acl.Viewers.Add(acceptingUserId);
        }
        else
        {
            if (!acl.Editors.Contains(acceptingUserId, StringComparer.OrdinalIgnoreCase))
                acl.Editors.Add(acceptingUserId);
            acl.Viewers.RemoveAll(v => string.Equals(v, acceptingUserId, StringComparison.OrdinalIgnoreCase));
        }
        acl.PendingInvites.RemoveAll(i => string.Equals(i.Token, token, StringComparison.Ordinal));
        acl.Rev++;
        await SaveAclAsync(projectId, acl, ct);
        return (true, projectId, null);
    }

    private async Task<ProjectAclDocument?> LoadAsync(string projectId, CancellationToken ct)
    {
        var path = AclPath(projectId);
        if (!File.Exists(path)) return null;
        await using var fs = File.OpenRead(path);
        var acl = await JsonSerializer.DeserializeAsync<ProjectAclDocument>(fs, JsonOpts, ct);
        if (acl is null) return null;
        acl.Editors ??= new List<string>();
        acl.Viewers ??= new List<string>();
        acl.PendingInvites ??= new List<PendingInvite>();
        return acl;
    }

    private string AclPath(string projectId) => Path.Combine(_projectsRoot, projectId, "project-acl.json");
    private static string InferOwner(string projectId)
    {
        var parts = projectId.Split('/', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : projectId;
    }
    private static void EnsureOwner(ProjectAclDocument acl, string callerUserId)
    {
        if (!string.Equals(acl.OwnerUserId, Norm(callerUserId), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only the project owner can modify ACL.");
    }
    private static string Norm(string? s) => (s ?? "").Trim();
    private static string CreateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    private static bool Matches(PendingInvite i, string? username, string? userId, string? email)
    {
        if (!string.IsNullOrWhiteSpace(userId) && string.Equals(i.Username, userId, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(username) && string.Equals(i.Username, username, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrWhiteSpace(email) && string.Equals(i.Email, email, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public sealed class PendingInvite
{
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string Role { get; set; } = "editor";
    public string Token { get; set; } = "";
    public string? InvitedBy { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSentUtc { get; set; }
}

public sealed class InviteResult
{
    public bool Ok { get; set; }
    public string? Status { get; set; }
    public string? UserId { get; set; }
    public string? Role { get; set; }
    public string? Token { get; set; }
    public string? InviteLink { get; set; }
    public bool EmailSent { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectUserInfo
{
    public string UserId { get; set; } = "";
    public string? Username { get; set; }
    public string? Email { get; set; }
}

public interface IProjectUserDirectory
{
    Task<ProjectUserInfo?> FindByIdAsync(string userId, CancellationToken ct = default);
    Task<ProjectUserInfo?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<ProjectUserInfo?> FindByEmailAsync(string email, CancellationToken ct = default);
}

public interface IProjectInviteMailer
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct = default);
}
