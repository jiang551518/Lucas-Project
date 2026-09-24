using System.Globalization;
using System.Data.Common;
using AgentBackend.Models;
using Microsoft.Data.Sqlite;

namespace AgentBackend.Repositories;

/// <summary>通过 SQLite 关系表持久化项目、会话、消息和设置。</summary>
public sealed class SqliteAgentRepository : IAgentRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteAgentRepository() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LucasAgent", "workspace.db")) { }

    /// <summary>创建结构化表，并清理开发阶段的单文档存储表。</summary>
    public SqliteAgentRepository(string databasePath)
    {
        var dataDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))
            ?? throw new ArgumentException("数据库路径无效。", nameof(databasePath));
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Projects (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL,
                WorkspacePath TEXT NOT NULL, WorkspacePathType TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Sessions (
                Id TEXT PRIMARY KEY, CreatedAt TEXT NOT NULL, Status TEXT NOT NULL,
                InitialPrompt TEXT, Title TEXT NOT NULL, ProjectId TEXT,
                WorkspacePath TEXT NOT NULL, WorkspacePathType TEXT NOT NULL,
                PermissionMode TEXT NOT NULL, UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (ProjectId) REFERENCES Projects(Id) ON DELETE SET NULL
            );
            CREATE TABLE IF NOT EXISTS Messages (
                Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, Position INTEGER NOT NULL,
                Role TEXT NOT NULL, Content TEXT NOT NULL, Reasoning TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, InputTokens INTEGER, OutputTokens INTEGER,
                ElapsedMilliseconds INTEGER, GitDiff TEXT,
                FOREIGN KEY (SessionId) REFERENCES Sessions(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Messages_SessionId_Position ON Messages(SessionId, Position);
            CREATE TABLE IF NOT EXISTS ProviderProfiles (
                Name TEXT PRIMARY KEY COLLATE NOCASE, BaseUrl TEXT NOT NULL,
                LegacyModel TEXT NOT NULL, ProtectedApiKey TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProviderModels (
                ProviderName TEXT NOT NULL, Position INTEGER NOT NULL, Model TEXT NOT NULL,
                PRIMARY KEY (ProviderName, Position),
                FOREIGN KEY (ProviderName) REFERENCES ProviderProfiles(Name) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS Skills (
                Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Description TEXT NOT NULL,
                Instructions TEXT NOT NULL, Enabled INTEGER NOT NULL, UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Memories (
                Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Content TEXT NOT NULL,
                Enabled INTEGER NOT NULL, UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS Settings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1), ShowReasoning INTEGER NOT NULL,
                ShowTokenUsage INTEGER NOT NULL, RequireToolApproval INTEGER NOT NULL
            );
            DROP TABLE IF EXISTS WorkspaceState;
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>读取所有项目，并按名称排序返回。</summary>
    public async Task<IReadOnlyList<AgentProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Projects.OrderBy(x => x.Name).ToArray();
    }

    /// <summary>创建项目并立即写入本地数据文件。</summary>
    public async Task<AgentProject> CreateProjectAsync(string name, string workspacePath, string workspacePathType, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var project = new AgentProject { Name = name.Trim(), WorkspacePath = workspacePath, WorkspacePathType = workspacePathType };
            data.Projects.Add(project);
            await SaveUnlockedAsync(data, cancellationToken);
            return project;
        }
        finally { _gate.Release(); }
    }

    /// <summary>更新项目显示名称及其关联工作区数据。</summary>
    public async Task<bool> RenameProjectAsync(Guid projectId, string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var project = data.Projects.FirstOrDefault(item => item.Id == projectId);
            if (project is null) return false;
            project.Name = name;
            await SaveUnlockedAsync(data, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>读取会话列表，并按最后更新时间倒序排列。</summary>
    public async Task<IReadOnlyList<AgentSession>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Sessions.OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    /// <summary>创建不带初始内容的空白会话。</summary>
    public Task<AgentSession> CreateSessionAsync(CancellationToken cancellationToken = default) => CreateSessionAsync(null, cancellationToken);

    /// <summary>创建会话；提供初始提示时会据此生成标题。</summary>
    public async Task<AgentSession> CreateSessionAsync(string? initialPrompt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var session = new AgentSession { InitialPrompt = initialPrompt, Title = string.IsNullOrWhiteSpace(initialPrompt) ? "新对话" : MakeTitle(initialPrompt) };
            data.Sessions.Add(session);
            await SaveUnlockedAsync(data, cancellationToken);
            return session;
        }
        finally { _gate.Release(); }
    }

    /// <summary>按 ID 查询会话及其消息。</summary>
    public async Task<AgentSession?> GetSessionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Sessions.FirstOrDefault(x => x.Id == id);
    }

    /// <summary>关联或解除会话与项目；项目或会话不存在时返回 false。</summary>
    public async Task<bool> AssignSessionAsync(Guid sessionId, Guid? projectId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var session = data.Sessions.FirstOrDefault(x => x.Id == sessionId);
            if (session is null || (projectId.HasValue && data.Projects.All(x => x.Id != projectId.Value))) return false;
            session.ProjectId = projectId;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(data, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>更新会话标题和最近更新时间。</summary>
    public async Task<bool> RenameSessionAsync(Guid sessionId, string title, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var session = data.Sessions.FirstOrDefault(item => item.Id == sessionId);
            if (session is null) return false;
            session.Title = title;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(data, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>更新会话的项目关联、工作区覆盖路径与工具权限模式。</summary>
    public async Task<bool> UpdateSessionExecutionContextAsync(Guid sessionId, Guid? projectId, string workspacePath, string workspacePathType, string permissionMode, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var session = data.Sessions.FirstOrDefault(item => item.Id == sessionId);
            if (session is null || (projectId.HasValue && data.Projects.All(item => item.Id != projectId.Value))) return false;
            session.ProjectId = projectId;
            var project = projectId.HasValue ? data.Projects.FirstOrDefault(item => item.Id == projectId.Value) : null;
            session.WorkspacePath = project?.WorkspacePath ?? workspacePath;
            session.WorkspacePathType = project?.WorkspacePathType ?? workspacePathType;
            session.PermissionMode = permissionMode;
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(data, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>追加消息、更新会话标题和更新时间，然后保存到磁盘。</summary>
    public async Task AddMessageAsync(Guid sessionId, AgentMessage message, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var session = data.Sessions.FirstOrDefault(x => x.Id == sessionId) ?? throw new KeyNotFoundException("会话不存在");
            session.Messages.Add(message);
            if (message.Role == "user" && session.Title == "新对话") session.Title = MakeTitle(message.Content);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveUnlockedAsync(data, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>保存指定消息的输入、输出 Token 数和运行耗时。</summary>
    public async Task UpdateUsageAsync(Guid messageId, int inputTokens, int outputTokens, long elapsedMilliseconds, string? gitDiff, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var message = data.Sessions.SelectMany(x => x.Messages).FirstOrDefault(x => x.Id == messageId);
            if (message is null) return;
            message.InputTokens = inputTokens;
            message.OutputTokens = outputTokens;
            message.ElapsedMilliseconds = elapsedMilliseconds;
            message.GitDiff = gitDiff;
            await SaveUnlockedAsync(data, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>删除会话及其消息历史，并持久化工作区。</summary>
    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var removed = data.Sessions.RemoveAll(session => session.Id == sessionId) > 0;
            if (removed) await SaveUnlockedAsync(data, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>删除项目，将其关联会话移回未归类最近列表后保存。</summary>
    public async Task<bool> DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var removed = data.Projects.RemoveAll(project => project.Id == projectId) > 0;
            if (!removed) return false;
            foreach (var session in data.Sessions.Where(session => session.ProjectId == projectId))
            {
                session.ProjectId = null;
                session.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await SaveUnlockedAsync(data, cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>读取本地 Provider 配置密文集合。</summary>
    public async Task<IReadOnlyList<StoredProviderProfile>> GetProviderProfilesAsync(CancellationToken cancellationToken = default)
    {
        var data = await ReadAsync(cancellationToken);
        return data.Providers.ToArray();
    }

    /// <summary>按当前或原 Provider 名称新增、更新或重命名本地配置。</summary>
    public async Task SaveProviderProfileAsync(StoredProviderProfile profile, string? existingName = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var lookupName = string.IsNullOrWhiteSpace(existingName) ? profile.Name : existingName.Trim();
            var existing = data.Providers.FirstOrDefault(item => item.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase));
            if (existing is null) data.Providers.Add(profile);
            else
            {
                data.Providers.Remove(existing);
                existing.BaseUrl = profile.BaseUrl;
                existing.Name = profile.Name;
                existing.Models = profile.Models;
                existing.Model = profile.Model;
                if (!string.IsNullOrWhiteSpace(profile.ProtectedApiKey)) existing.ProtectedApiKey = profile.ProtectedApiKey;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                data.Providers.Add(existing);
            }
            await SaveUnlockedAsync(data, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>删除本地 Provider 覆盖项；appsettings 中的默认项不会改变。</summary>
    public async Task<bool> DeleteProviderProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var removed = data.Providers.RemoveAll(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await SaveUnlockedAsync(data, cancellationToken);
            return removed;
        }
        finally { _gate.Release(); }
    }

    /// <summary>返回最近更新的本地技能。</summary>
    public async Task<IReadOnlyList<AgentSkill>> GetSkillsAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Skills.OrderByDescending(item => item.UpdatedAt).ToArray();

    /// <summary>以稳定 ID 新增或更新技能，并保存整个工作区。</summary>
    public async Task<AgentSkill> SaveSkillAsync(AgentSkill skill, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var existing = data.Skills.FirstOrDefault(item => item.Id == skill.Id);
            skill.UpdatedAt = DateTimeOffset.UtcNow;
            if (existing is null) data.Skills.Add(skill);
            else { existing.Name = skill.Name; existing.Description = skill.Description; existing.Instructions = skill.Instructions; existing.Enabled = skill.Enabled; existing.UpdatedAt = skill.UpdatedAt; }
            await SaveUnlockedAsync(data, cancellationToken);
            return existing ?? skill;
        }
        finally { _gate.Release(); }
    }

    /// <summary>删除指定技能。</summary>
    public async Task<bool> DeleteSkillAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { var data = await ReadUnlockedAsync(cancellationToken); var removed = data.Skills.RemoveAll(item => item.Id == id) > 0; if (removed) await SaveUnlockedAsync(data, cancellationToken); return removed; }
        finally { _gate.Release(); }
    }

    /// <summary>返回最近更新的本地记忆。</summary>
    public async Task<IReadOnlyList<MemoryEntry>> GetMemoriesAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Memories.OrderByDescending(item => item.UpdatedAt).ToArray();

    /// <summary>以稳定 ID 新增或更新记忆条目，并保存整个工作区。</summary>
    public async Task<MemoryEntry> SaveMemoryAsync(MemoryEntry memory, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var data = await ReadUnlockedAsync(cancellationToken);
            var existing = data.Memories.FirstOrDefault(item => item.Id == memory.Id);
            memory.UpdatedAt = DateTimeOffset.UtcNow;
            if (existing is null) data.Memories.Add(memory);
            else { existing.Title = memory.Title; existing.Content = memory.Content; existing.Enabled = memory.Enabled; existing.UpdatedAt = memory.UpdatedAt; }
            await SaveUnlockedAsync(data, cancellationToken);
            return existing ?? memory;
        }
        finally { _gate.Release(); }
    }

    /// <summary>删除指定记忆条目。</summary>
    public async Task<bool> DeleteMemoryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { var data = await ReadUnlockedAsync(cancellationToken); var removed = data.Memories.RemoveAll(item => item.Id == id) > 0; if (removed) await SaveUnlockedAsync(data, cancellationToken); return removed; }
        finally { _gate.Release(); }
    }

    /// <summary>读取本地偏好；旧工作区缺少该字段时返回默认设置。</summary>
    public async Task<AgentSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        (await ReadAsync(cancellationToken)).Settings ?? new AgentSettings();

    /// <summary>替换本地设置并保存整个工作区。</summary>
    public async Task SaveSettingsAsync(AgentSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { var data = await ReadUnlockedAsync(cancellationToken); data.Settings = settings; await SaveUnlockedAsync(data, cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <summary>在异步锁保护下读取工作区数据。</summary>
    private async Task<WorkspaceData> ReadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await ReadUnlockedAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <summary>从各关系表读取工作区数据，供现有业务方法操作。</summary>
    private async Task<WorkspaceData> ReadUnlockedAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var data = new WorkspaceData();

        await ReadRowsAsync(connection, transaction,
            "SELECT Id, Name, CreatedAt, WorkspacePath, WorkspacePathType FROM Projects;",
            row => data.Projects.Add(new AgentProject
            {
                Id = Guid.Parse(row.GetString(0)),
                Name = row.GetString(1),
                CreatedAt = ParseTimestamp(row.GetString(2)),
                WorkspacePath = row.GetString(3),
                WorkspacePathType = row.GetString(4)
            }), cancellationToken);

        var sessions = new Dictionary<Guid, AgentSession>();
        await ReadRowsAsync(connection, transaction,
            "SELECT Id, CreatedAt, Status, InitialPrompt, Title, ProjectId, WorkspacePath, WorkspacePathType, PermissionMode, UpdatedAt FROM Sessions;",
            row =>
            {
                var session = new AgentSession
                {
                    Id = Guid.Parse(row.GetString(0)),
                    CreatedAt = ParseTimestamp(row.GetString(1)),
                    Status = row.GetString(2),
                    InitialPrompt = row.IsDBNull(3) ? null : row.GetString(3),
                    Title = row.GetString(4),
                    ProjectId = row.IsDBNull(5) ? null : Guid.Parse(row.GetString(5)),
                    WorkspacePath = row.GetString(6),
                    WorkspacePathType = row.GetString(7),
                    PermissionMode = row.GetString(8),
                    UpdatedAt = ParseTimestamp(row.GetString(9))
                };
                data.Sessions.Add(session);
                sessions.Add(session.Id, session);
            }, cancellationToken);

        await ReadRowsAsync(connection, transaction,
            "SELECT Id, SessionId, Role, Content, Reasoning, CreatedAt, InputTokens, OutputTokens, ElapsedMilliseconds, GitDiff FROM Messages ORDER BY SessionId, Position;",
            row =>
            {
                var message = new AgentMessage
                {
                    Id = Guid.Parse(row.GetString(0)),
                    Role = row.GetString(2),
                    Content = row.GetString(3),
                    Reasoning = row.GetString(4),
                    CreatedAt = ParseTimestamp(row.GetString(5)),
                    InputTokens = row.IsDBNull(6) ? null : checked((int)row.GetInt64(6)),
                    OutputTokens = row.IsDBNull(7) ? null : checked((int)row.GetInt64(7)),
                    ElapsedMilliseconds = row.IsDBNull(8) ? null : row.GetInt64(8),
                    GitDiff = row.IsDBNull(9) ? null : row.GetString(9)
                };
                sessions[Guid.Parse(row.GetString(1))].Messages.Add(message);
            }, cancellationToken);

        var providers = new Dictionary<string, StoredProviderProfile>(StringComparer.OrdinalIgnoreCase);
        await ReadRowsAsync(connection, transaction,
            "SELECT Name, BaseUrl, LegacyModel, ProtectedApiKey, UpdatedAt FROM ProviderProfiles;",
            row =>
            {
                var profile = new StoredProviderProfile
                {
                    Name = row.GetString(0),
                    BaseUrl = row.GetString(1),
                    Model = row.GetString(2),
                    ProtectedApiKey = row.GetString(3),
                    UpdatedAt = ParseTimestamp(row.GetString(4))
                };
                data.Providers.Add(profile);
                providers.Add(profile.Name, profile);
            }, cancellationToken);
        await ReadRowsAsync(connection, transaction,
            "SELECT ProviderName, Model FROM ProviderModels ORDER BY ProviderName, Position;",
            row => providers[row.GetString(0)].Models.Add(row.GetString(1)), cancellationToken);

        await ReadRowsAsync(connection, transaction,
            "SELECT Id, Name, Description, Instructions, Enabled, UpdatedAt FROM Skills;",
            row => data.Skills.Add(new AgentSkill
            {
                Id = Guid.Parse(row.GetString(0)),
                Name = row.GetString(1),
                Description = row.GetString(2),
                Instructions = row.GetString(3),
                Enabled = row.GetInt64(4) != 0,
                UpdatedAt = ParseTimestamp(row.GetString(5))
            }), cancellationToken);
        await ReadRowsAsync(connection, transaction,
            "SELECT Id, Title, Content, Enabled, UpdatedAt FROM Memories;",
            row => data.Memories.Add(new MemoryEntry
            {
                Id = Guid.Parse(row.GetString(0)),
                Title = row.GetString(1),
                Content = row.GetString(2),
                Enabled = row.GetInt64(3) != 0,
                UpdatedAt = ParseTimestamp(row.GetString(4))
            }), cancellationToken);
        await ReadRowsAsync(connection, transaction,
            "SELECT ShowReasoning, ShowTokenUsage, RequireToolApproval FROM Settings WHERE Id = 1;",
            row => data.Settings = new AgentSettings
            {
                ShowReasoning = row.GetInt64(0) != 0,
                ShowTokenUsage = row.GetInt64(1) != 0,
                RequireToolApproval = row.GetInt64(2) != 0
            }, cancellationToken);

        transaction.Commit();
        return data;
    }

    /// <summary>在单个 SQLite 事务中保存关系表，保持现有工作区操作的原子性。</summary>
    private async Task SaveUnlockedAsync(WorkspaceData data, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction, """
            DELETE FROM Messages;
            DELETE FROM Sessions;
            DELETE FROM Projects;
            DELETE FROM ProviderModels;
            DELETE FROM ProviderProfiles;
            DELETE FROM Skills;
            DELETE FROM Memories;
            DELETE FROM Settings;
            """, cancellationToken);

        foreach (var project in data.Projects)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Projects (Id, Name, CreatedAt, WorkspacePath, WorkspacePathType) VALUES ($id, $name, $createdAt, $workspacePath, $workspacePathType);",
                cancellationToken,
                ("$id", project.Id.ToString()), ("$name", project.Name), ("$createdAt", FormatTimestamp(project.CreatedAt)),
                ("$workspacePath", project.WorkspacePath), ("$workspacePathType", project.WorkspacePathType));

        foreach (var session in data.Sessions)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Sessions (Id, CreatedAt, Status, InitialPrompt, Title, ProjectId, WorkspacePath, WorkspacePathType, PermissionMode, UpdatedAt) VALUES ($id, $createdAt, $status, $initialPrompt, $title, $projectId, $workspacePath, $workspacePathType, $permissionMode, $updatedAt);",
                cancellationToken,
                ("$id", session.Id.ToString()), ("$createdAt", FormatTimestamp(session.CreatedAt)),
                ("$status", session.Status), ("$initialPrompt", session.InitialPrompt), ("$title", session.Title),
                ("$projectId", session.ProjectId?.ToString()), ("$workspacePath", session.WorkspacePath),
                ("$workspacePathType", session.WorkspacePathType), ("$permissionMode", session.PermissionMode),
                ("$updatedAt", FormatTimestamp(session.UpdatedAt)));
            for (var position = 0; position < session.Messages.Count; position++)
            {
                var message = session.Messages[position];
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO Messages (Id, SessionId, Position, Role, Content, Reasoning, CreatedAt, InputTokens, OutputTokens, ElapsedMilliseconds, GitDiff) VALUES ($id, $sessionId, $position, $role, $content, $reasoning, $createdAt, $inputTokens, $outputTokens, $elapsedMilliseconds, $gitDiff);",
                    cancellationToken,
                    ("$id", message.Id.ToString()), ("$sessionId", session.Id.ToString()), ("$position", position),
                    ("$role", message.Role), ("$content", message.Content), ("$reasoning", message.Reasoning),
                    ("$createdAt", FormatTimestamp(message.CreatedAt)), ("$inputTokens", message.InputTokens),
                    ("$outputTokens", message.OutputTokens), ("$elapsedMilliseconds", message.ElapsedMilliseconds),
                    ("$gitDiff", message.GitDiff));
            }
        }

        foreach (var provider in data.Providers)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO ProviderProfiles (Name, BaseUrl, LegacyModel, ProtectedApiKey, UpdatedAt) VALUES ($name, $baseUrl, $legacyModel, $protectedApiKey, $updatedAt);",
                cancellationToken,
                ("$name", provider.Name), ("$baseUrl", provider.BaseUrl), ("$legacyModel", provider.Model),
                ("$protectedApiKey", provider.ProtectedApiKey), ("$updatedAt", FormatTimestamp(provider.UpdatedAt)));
            for (var position = 0; position < provider.Models.Count; position++)
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO ProviderModels (ProviderName, Position, Model) VALUES ($providerName, $position, $model);",
                    cancellationToken,
                    ("$providerName", provider.Name), ("$position", position), ("$model", provider.Models[position]));
        }

        foreach (var skill in data.Skills)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Skills (Id, Name, Description, Instructions, Enabled, UpdatedAt) VALUES ($id, $name, $description, $instructions, $enabled, $updatedAt);",
                cancellationToken,
                ("$id", skill.Id.ToString()), ("$name", skill.Name), ("$description", skill.Description),
                ("$instructions", skill.Instructions), ("$enabled", skill.Enabled ? 1 : 0),
                ("$updatedAt", FormatTimestamp(skill.UpdatedAt)));

        foreach (var memory in data.Memories)
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Memories (Id, Title, Content, Enabled, UpdatedAt) VALUES ($id, $title, $content, $enabled, $updatedAt);",
                cancellationToken,
                ("$id", memory.Id.ToString()), ("$title", memory.Title), ("$content", memory.Content),
                ("$enabled", memory.Enabled ? 1 : 0), ("$updatedAt", FormatTimestamp(memory.UpdatedAt)));

        await ExecuteAsync(connection, transaction,
            "INSERT INTO Settings (Id, ShowReasoning, ShowTokenUsage, RequireToolApproval) VALUES (1, $showReasoning, $showTokenUsage, $requireToolApproval);",
            cancellationToken,
            ("$showReasoning", data.Settings.ShowReasoning ? 1 : 0),
            ("$showTokenUsage", data.Settings.ShowTokenUsage ? 1 : 0),
            ("$requireToolApproval", data.Settings.RequireToolApproval ? 1 : 0));
        transaction.Commit();
    }

    /// <summary>执行只读 SQL 查询，并对每一行调用指定的映射回调。</summary>
    private static async Task ReadRowsAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, Action<DbDataReader> readRow, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) readRow(reader);
    }

    /// <summary>在给定事务中执行参数化 SQL 写入语句。</summary>
    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>按往返格式解析数据库中保存的时间戳。</summary>
    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>将时间戳格式化为文化无关的往返字符串。</summary>
    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>将首条用户消息截断为侧栏可显示的会话标题。</summary>
    private static string MakeTitle(string text) => text.Length <= 36 ? text : text[..36] + "…";
}
