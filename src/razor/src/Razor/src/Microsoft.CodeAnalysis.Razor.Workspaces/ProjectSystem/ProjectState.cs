﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.AspNetCore.Razor;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.ProjectEngineHost;
using Microsoft.AspNetCore.Razor.ProjectSystem;
using Microsoft.AspNetCore.Razor.Utilities;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.CodeAnalysis.Text;
using Microsoft.NET.Sdk.Razor.SourceGenerators;

namespace Microsoft.CodeAnalysis.Razor.ProjectSystem;

// Internal tracker for DefaultProjectSnapshot
internal class ProjectState
{
    private const ProjectDifference ClearConfigurationVersionMask = ProjectDifference.ConfigurationChanged;

    private const ProjectDifference ClearProjectWorkspaceStateVersionMask =
        ProjectDifference.ConfigurationChanged |
        ProjectDifference.ProjectWorkspaceStateChanged;

    private const ProjectDifference ClearDocumentCollectionVersionMask =
        ProjectDifference.ConfigurationChanged |
        ProjectDifference.DocumentAdded |
        ProjectDifference.DocumentRemoved;

    private static readonly ImmutableDictionary<string, DocumentState> s_emptyDocuments = ImmutableDictionary.Create<string, DocumentState>(FilePathNormalizingComparer.Instance);
    private static readonly ImmutableDictionary<string, ImmutableArray<string>> s_emptyImportsToRelatedDocuments = ImmutableDictionary.Create<string, ImmutableArray<string>>(FilePathNormalizingComparer.Instance);
    private readonly object _lock;

    private readonly IProjectEngineFactoryProvider _projectEngineFactoryProvider;
    private readonly LanguageServerFeatureOptions _languageServerFeatureOptions;
    private RazorProjectEngine? _projectEngine;

    public static ProjectState Create(
        IProjectEngineFactoryProvider projectEngineFactoryProvider,
        LanguageServerFeatureOptions languageServerFeatureOptions,
        HostProject hostProject,
        ProjectWorkspaceState projectWorkspaceState)
    {
        return new ProjectState(projectEngineFactoryProvider, languageServerFeatureOptions, hostProject, projectWorkspaceState);
    }

    private ProjectState(
        IProjectEngineFactoryProvider projectEngineFactoryProvider,
        LanguageServerFeatureOptions languageServerFeatureOptions,
        HostProject hostProject,
        ProjectWorkspaceState projectWorkspaceState)
    {
        _projectEngineFactoryProvider = projectEngineFactoryProvider;
        _languageServerFeatureOptions = languageServerFeatureOptions;
        HostProject = hostProject;
        ProjectWorkspaceState = projectWorkspaceState;
        Documents = s_emptyDocuments;
        ImportsToRelatedDocuments = s_emptyImportsToRelatedDocuments;
        Version = VersionStamp.Create();
        ProjectWorkspaceStateVersion = Version;
        DocumentCollectionVersion = Version;

        _lock = new object();
    }

    private ProjectState(
        ProjectState older,
        ProjectDifference difference,
        HostProject hostProject,
        ProjectWorkspaceState projectWorkspaceState,
        ImmutableDictionary<string, DocumentState> documents,
        ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments)
    {
        _projectEngineFactoryProvider = older._projectEngineFactoryProvider;
        _languageServerFeatureOptions = older._languageServerFeatureOptions;
        Version = older.Version.GetNewerVersion();

        HostProject = hostProject;
        ProjectWorkspaceState = projectWorkspaceState;
        Documents = documents;
        ImportsToRelatedDocuments = importsToRelatedDocuments;

        _lock = new object();

        if ((difference & ClearDocumentCollectionVersionMask) == 0)
        {
            // Document collection hasn't changed
            DocumentCollectionVersion = older.DocumentCollectionVersion;
        }
        else
        {
            DocumentCollectionVersion = Version;
        }

        if ((difference & ClearConfigurationVersionMask) == 0 && older._projectEngine != null)
        {
            // Optimistically cache the RazorProjectEngine.
            _projectEngine = older.ProjectEngine;
            ConfigurationVersion = older.ConfigurationVersion;
        }
        else
        {
            ConfigurationVersion = Version;
        }

        if ((difference & ClearProjectWorkspaceStateVersionMask) == 0 ||
            ProjectWorkspaceState == older.ProjectWorkspaceState ||
            ProjectWorkspaceState.Equals(older.ProjectWorkspaceState))
        {
            ProjectWorkspaceStateVersion = older.ProjectWorkspaceStateVersion;
        }
        else
        {
            ProjectWorkspaceStateVersion = Version;
        }

        if ((difference & ClearProjectWorkspaceStateVersionMask) != 0 &&
            CSharpLanguageVersion != older.CSharpLanguageVersion)
        {
            // C# language version changed. This impacts the ProjectEngine, reset it.
            _projectEngine = null;
            ConfigurationVersion = Version;
        }
    }

    // Internal set for testing.
    public ImmutableDictionary<string, DocumentState> Documents { get; internal set; }

    // Internal set for testing.
    public ImmutableDictionary<string, ImmutableArray<string>> ImportsToRelatedDocuments { get; internal set; }

    public HostProject HostProject { get; }

    internal LanguageServerFeatureOptions LanguageServerFeatureOptions => _languageServerFeatureOptions;

    public ProjectWorkspaceState ProjectWorkspaceState { get; }

    public ImmutableArray<TagHelperDescriptor> TagHelpers => ProjectWorkspaceState.TagHelpers;

    public LanguageVersion CSharpLanguageVersion => ProjectWorkspaceState.CSharpLanguageVersion;

    /// <summary>
    /// Gets the version of this project, INCLUDING content changes. The <see cref="Version"/> is
    /// incremented for each new <see cref="ProjectState"/> instance created.
    /// </summary>
    public VersionStamp Version { get; }

    /// <summary>
    /// Gets the version of this project, NOT INCLUDING computed or content changes. The
    /// <see cref="DocumentCollectionVersion"/> is incremented each time the configuration changes or
    /// a document is added or removed.
    /// </summary>
    public VersionStamp DocumentCollectionVersion { get; }

    public RazorProjectEngine ProjectEngine
    {
        get
        {
            lock (_lock)
            {
                _projectEngine ??= CreateProjectEngine();
            }

            return _projectEngine;

            RazorProjectEngine CreateProjectEngine()
            {
                var configuration = HostProject.Configuration;
                var rootDirectoryPath = Path.GetDirectoryName(HostProject.FilePath).AssumeNotNull();
                var useRoslynTokenizer = LanguageServerFeatureOptions.UseRoslynTokenizer;

                return _projectEngineFactoryProvider.Create(configuration, rootDirectoryPath, builder =>
                {
                    builder.SetRootNamespace(HostProject.RootNamespace);
                    builder.SetCSharpLanguageVersion(CSharpLanguageVersion);
                    builder.SetSupportLocalizedComponentNames();
                    builder.Features.Add(new ConfigureRazorParserOptions(useRoslynTokenizer, CSharpParseOptions.Default));
                });
            }
        }
    }

    /// <summary>
    /// Gets the version of this project based on the project workspace state, NOT INCLUDING content
    /// changes. The computed state is guaranteed to change when the configuration or tag helpers
    /// change.
    /// </summary>
    public VersionStamp ProjectWorkspaceStateVersion { get; }

    public VersionStamp ConfigurationVersion { get; }

    public ProjectState AddDocument(HostDocument hostDocument, SourceText text)
    {
        ArgHelper.ThrowIfNull(hostDocument);
        ArgHelper.ThrowIfNull(text);

        // Ignore attempts to 'add' a document with different data, we only
        // care about one, so it might as well be the one we have.
        if (Documents.ContainsKey(hostDocument.FilePath))
        {
            return this;
        }

        var state = DocumentState.Create(hostDocument, version: 1, text, VersionStamp.Create());

        return AddDocument(state);
    }

    public ProjectState AddDocument(HostDocument hostDocument, TextLoader textLoader)
    {
        ArgHelper.ThrowIfNull(hostDocument);
        ArgHelper.ThrowIfNull(textLoader);

        // Ignore attempts to 'add' a document with different data, we only
        // care about one, so it might as well be the one we have.
        if (Documents.ContainsKey(hostDocument.FilePath))
        {
            return this;
        }

        var state = DocumentState.Create(hostDocument, version: 1, textLoader);

        return AddDocument(state);
    }

    private ProjectState AddDocument(DocumentState state)
    {
        var hostDocument = state.HostDocument;
        var documents = Documents.Add(hostDocument.FilePath, state);

        // Compute the effect on the import map
        var importTargetPaths = GetImportDocumentTargetPaths(hostDocument);
        var importsToRelatedDocuments = AddToImportsToRelatedDocuments(ImportsToRelatedDocuments, hostDocument.FilePath, importTargetPaths);

        // Now check if the updated document is an import - it's important this this happens after
        // updating the imports map.
        if (importsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
        {
            foreach (var relatedDocument in relatedDocuments)
            {
                documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
            }
        }

        return new(this, ProjectDifference.DocumentAdded, HostProject, ProjectWorkspaceState, documents, importsToRelatedDocuments);
    }

    public ProjectState RemoveDocument(string documentFilePath)
    {
        ArgHelper.ThrowIfNull(documentFilePath);

        if (!Documents.TryGetValue(documentFilePath, out var state))
        {
            return this;
        }

        var hostDocument = state.HostDocument;

        var documents = Documents.Remove(documentFilePath);

        // First check if the updated document is an import - it's important that this happens
        // before updating the imports map.
        if (ImportsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
        {
            foreach (var relatedDocument in relatedDocuments)
            {
                documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
            }
        }

        // Compute the effect on the import map
        var importTargetPaths = GetImportDocumentTargetPaths(hostDocument);
        var importsToRelatedDocuments = RemoveFromImportsToRelatedDocuments(ImportsToRelatedDocuments, hostDocument, importTargetPaths);

        return new(this, ProjectDifference.DocumentRemoved, HostProject, ProjectWorkspaceState, documents, importsToRelatedDocuments);
    }

    public ProjectState WithDocumentText(string documentFilePath, SourceText text)
    {
        ArgHelper.ThrowIfNull(documentFilePath);
        ArgHelper.ThrowIfNull(text);

        if (!Documents.TryGetValue(documentFilePath, out var oldState))
        {
            return this;
        }

        if (oldState.TryGetTextAndVersion(out var oldTextAndVersion))
        {
            var newVersion = text.ContentEquals(oldTextAndVersion.Text)
                ? oldTextAndVersion.Version
                : oldTextAndVersion.Version.GetNewerVersion();

            return WithDocumentText(oldState, state => state.WithText(text, newVersion));
        }

        return WithDocumentText(oldState, state => state.WithTextLoader(new UpdatedTextLoader(state, text)));
    }

    public ProjectState WithDocumentText(string documentFilePath, TextLoader textLoader)
    {
        ArgHelper.ThrowIfNull(documentFilePath);

        if (!Documents.TryGetValue(documentFilePath, out var state))
        {
            return this;
        }

        return WithDocumentText(state, state => state.WithTextLoader(textLoader));
    }

    private ProjectState WithDocumentText(DocumentState state, Func<DocumentState, DocumentState> transformer)
    {
        var hostDocument = state.HostDocument;
        var documents = Documents.SetItem(hostDocument.FilePath, transformer(state));

        // If this document is an import, update its related documents.
        if (ImportsToRelatedDocuments.TryGetValue(hostDocument.TargetPath, out var relatedDocuments))
        {
            foreach (var relatedDocument in relatedDocuments)
            {
                documents = documents.SetItem(relatedDocument, documents[relatedDocument].WithImportsChange());
            }
        }

        return new(this, ProjectDifference.DocumentChanged, HostProject, ProjectWorkspaceState, documents, ImportsToRelatedDocuments);
    }

    public ProjectState WithHostProject(HostProject hostProject)
    {
        if (hostProject is null)
        {
            throw new ArgumentNullException(nameof(hostProject));
        }

        if (HostProject.Configuration.Equals(hostProject.Configuration) &&
            HostProject.RootNamespace == hostProject.RootNamespace)
        {
            return this;
        }

        var documents = Documents.ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Value.WithConfigurationChange(), FilePathNormalizingComparer.Instance);

        // If the host project has changed then we need to recompute the imports map
        var importsToRelatedDocuments = s_emptyImportsToRelatedDocuments;

        foreach (var document in documents)
        {
            var importTargetPaths = GetImportDocumentTargetPaths(document.Value.HostDocument);
            importsToRelatedDocuments = AddToImportsToRelatedDocuments(importsToRelatedDocuments, document.Value.HostDocument.FilePath, importTargetPaths);
        }

        var state = new ProjectState(this, ProjectDifference.ConfigurationChanged, hostProject, ProjectWorkspaceState, documents, importsToRelatedDocuments);
        return state;
    }

    public ProjectState WithProjectWorkspaceState(ProjectWorkspaceState projectWorkspaceState)
    {
        ArgHelper.ThrowIfNull(projectWorkspaceState);

        if (ProjectWorkspaceState == projectWorkspaceState)
        {
            return this;
        }

        if (ProjectWorkspaceState.Equals(projectWorkspaceState))
        {
            return this;
        }

        var documents = Documents.ToImmutableDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.WithProjectWorkspaceStateChange(),
            FilePathNormalizingComparer.Instance);

        return new(this, ProjectDifference.ProjectWorkspaceStateChanged, HostProject, projectWorkspaceState, documents, ImportsToRelatedDocuments);
    }

    internal static ImmutableDictionary<string, ImmutableArray<string>> AddToImportsToRelatedDocuments(
        ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments,
        string documentFilePath,
        List<string> importTargetPaths)
    {
        foreach (var importTargetPath in importTargetPaths)
        {
            if (!importsToRelatedDocuments.TryGetValue(importTargetPath, out var relatedDocuments))
            {
                relatedDocuments = ImmutableArray.Create<string>();
            }

            relatedDocuments = relatedDocuments.Add(documentFilePath);
            importsToRelatedDocuments = importsToRelatedDocuments.SetItem(importTargetPath, relatedDocuments);
        }

        return importsToRelatedDocuments;
    }

    private static ImmutableDictionary<string, ImmutableArray<string>> RemoveFromImportsToRelatedDocuments(
        ImmutableDictionary<string, ImmutableArray<string>> importsToRelatedDocuments,
        HostDocument hostDocument,
        List<string> importTargetPaths)
    {
        foreach (var importTargetPath in importTargetPaths)
        {
            if (importsToRelatedDocuments.TryGetValue(importTargetPath, out var relatedDocuments))
            {
                relatedDocuments = relatedDocuments.Remove(hostDocument.FilePath);
                importsToRelatedDocuments = relatedDocuments.Length > 0
                    ? importsToRelatedDocuments.SetItem(importTargetPath, relatedDocuments)
                    : importsToRelatedDocuments.Remove(importTargetPath);
            }
        }

        return importsToRelatedDocuments;
    }

    public List<string> GetImportDocumentTargetPaths(HostDocument hostDocument)
    {
        return GetImportDocumentTargetPaths(hostDocument.TargetPath, hostDocument.FileKind, ProjectEngine);
    }

    internal static List<string> GetImportDocumentTargetPaths(string targetPath, string fileKind, RazorProjectEngine projectEngine)
    {
        var importFeatures = projectEngine.ProjectFeatures.OfType<IImportProjectFeature>();
        var projectItem = projectEngine.FileSystem.GetItem(targetPath, fileKind);
        var importItems = importFeatures.SelectMany(f => f.GetImports(projectItem)).Where(i => i.FilePath != null);

        // Target path looks like `Foo\\Bar.cshtml`
        var targetPaths = new List<string>();
        foreach (var importItem in importItems)
        {
            var itemTargetPath = importItem.FilePath.Replace('/', '\\').TrimStart('\\');

            if (FilePathNormalizingComparer.Instance.Equals(itemTargetPath, targetPath))
            {
                // We've normalized the original importItem.FilePath into the HostDocument.TargetPath. For instance, if the HostDocument.TargetPath
                // was '/_Imports.razor' it'd be normalized down into '_Imports.razor'. The purpose of this method is to get the associated document
                // paths for a given import file (_Imports.razor / _ViewImports.cshtml); therefore, an import importing itself doesn't make sense.
                continue;
            }

            targetPaths.Add(itemTargetPath);
        }

        return targetPaths;
    }

    private sealed class UpdatedTextLoader(DocumentState oldState, SourceText text) : TextLoader
    {
        public override async Task<TextAndVersion> LoadTextAndVersionAsync(LoadTextOptions options, CancellationToken cancellationToken)
        {
            var oldTextAndVersion = await oldState.GetTextAndVersionAsync(cancellationToken).ConfigureAwait(false);

            var newVersion = text.ContentEquals(oldTextAndVersion.Text)
                ? oldTextAndVersion.Version
                : oldTextAndVersion.Version.GetNewerVersion();

            return TextAndVersion.Create(text, newVersion);
        }
    }
}
