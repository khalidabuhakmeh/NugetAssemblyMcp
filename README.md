# NuGetAssemblyMcp

An MCP (Model Context Protocol) server that enables AI assistants to inspect and analyze .NET NuGet packages. Download packages, explore types and members, read XML documentation, and resolve source links—all through MCP tools.

## Features

- **Package Management** — Download and cache NuGet packages with automatic version resolution
- **Private Feed Support** — Load solution-specific NuGet.config files to access private/authenticated feeds
- **Package Metadata** — View descriptions, licenses, dependencies, vulnerabilities, and deprecation status
- **Assembly Inspection** — Explore namespaces, types, and members using Mono.Cecil (safe IL-level reflection)
- **XML Documentation** — Extract summaries, parameter docs, and examples from XML doc files
- **SourceLink Integration** — Resolve repository URLs for browsing source code on GitHub, GitLab, or Azure DevOps
- **Smart Framework Selection** — Automatically selects the best target framework (net10.0 → netstandard → net48)
- **Regex Search** — Find types by pattern matching

## Installation

### Option 1: Download Pre-built Binary (Recommended)

Download the latest release for your platform from [GitHub Releases](https://github.com/your-repo/NuGetAssemblyMcp/releases):

| Platform | File |
|----------|------|
| Windows x64 | `NuGetAssemblyMcp-win-x64.exe` |
| Linux x64 | `NuGetAssemblyMcp-linux-x64` |
| macOS x64 (Intel) | `NuGetAssemblyMcp-osx-x64` |
| macOS ARM64 (Apple Silicon) | `NuGetAssemblyMcp-osx-arm64` |

These are self-contained single-file executables—no .NET runtime required.

**Linux/macOS:** Make the binary executable after downloading:
```bash
chmod +x NuGetAssemblyMcp-osx-arm64
```

**macOS Gatekeeper:** The pre-built binaries are ad-hoc signed (no Apple Developer ID). macOS will quarantine downloaded binaries and Gatekeeper will block them from running — especially when spawned as a subprocess by MCP clients (Claude Desktop, opencode, etc.), even if they appear to work when run directly from Terminal.

You **must** remove the quarantine attribute after downloading:

```bash
# Remove quarantine from the binary and all supporting files
xattr -cr /path/to/nuget-assembly-mcp/
```

Or for a single binary:
```bash
xattr -d com.apple.quarantine /path/to/NuGetAssemblyMcp-osx-arm64
```

**Symptoms if you skip this step:**
- MCP server fails to start with no clear error message
- The binary works fine when run directly in Terminal but fails when launched by an MCP client
- `spctl --assess` reports the binary as "rejected"

You can verify the fix worked:
```bash
# Should produce no output (no quarantine attributes)
xattr /path/to/NuGetAssemblyMcp-osx-arm64
```

### Option 2: Build from Source

Prerequisites: [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later

```bash
git clone https://github.com/your-repo/NuGetAssemblyMcp.git
cd NuGetAssemblyMcp
dotnet build -c Release
```

To create your own single-file executable:
```bash
dotnet publish src/NuGetAssemblyMcp/NuGetAssemblyMcp.csproj \
  -c Release \
  -r osx-arm64 \
  -p:PublishSingleFile=true \
  -p:SelfContained=true \
  -o ./publish
```

### Configure Your MCP Client

Add the server to your MCP client configuration. For example, in Claude Desktop's `claude_desktop_config.json`:

**Using a pre-built binary (recommended):**
```json
{
  "mcpServers": {
    "nuget-assembly": {
      "command": "/path/to/NuGetAssemblyMcp-osx-arm64"
    }
  }
}
```

**Using dotnet run (development):**
```json
{
  "mcpServers": {
    "nuget-assembly": {
      "command": "dotnet",
      "args": [
        "run",
        "--project",
        "/path/to/NuGetAssemblyMcp/src/NuGetAssemblyMcp/NuGetAssemblyMcp.csproj"
      ]
    }
  }
}
```

## MCP Tools

### Package Tools

#### `set_working_directory`
Sets the working directory for NuGet.config lookup. Use this to load solution-specific package sources (e.g., private feeds).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `directoryPath` | string | No | Directory path for NuGet.config lookup (typically solution root). Pass empty/null to reset to default. |

**Returns:** Confirmation with the current working directory path.

#### `list_package_versions`
Lists all available versions of a NuGet package (newest first).

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID (e.g., "Newtonsoft.Json") |

#### `load_package`
Downloads and caches a NuGet package, returning metadata about the package contents.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `version` | string | No | Specific version; defaults to latest stable |
| `targetFramework` | string | No | Target framework (e.g., "net8.0"); auto-selected if omitted |

**Returns:** Package metadata including repository URL, commit hash, and file availability.

#### `get_package_metadata`
Gets detailed package metadata from NuGet sources—similar to what you'd see on NuGet.org.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `version` | string | No | Specific version; defaults to latest stable |

**Returns:** Description, authors, license, project URL, dependencies, vulnerabilities, and deprecation status.

#### `list_sources`
Lists the currently configured NuGet package sources based on the working directory context.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| _(none)_ | — | — | Uses the current working directory set by `set_working_directory` |

**Returns:** List of package sources with name, URL, enabled status, and whether they're machine-wide or official.

### Assembly Tools

#### `list_namespaces`
Lists all public namespaces in a package's primary assembly.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `version` | string | No | Package version |
| `targetFramework` | string | No | Target framework moniker |

#### `list_types`
Lists types (classes, interfaces, structs, enums, delegates) in a package assembly.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `ns` | string | No | Namespace filter |
| `version` | string | No | Package version |
| `targetFramework` | string | No | Target framework moniker |

#### `get_type_info`
Returns comprehensive information about a specific type including members, XML documentation, and source link.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `typeFullName` | string | Yes | Fully-qualified type name (e.g., "Newtonsoft.Json.JsonConvert") |
| `version` | string | No | Package version |
| `targetFramework` | string | No | Target framework moniker |

**Returns:** Markdown document with type metadata, constructors, properties, methods, events, fields, and documentation.

#### `get_member_info`
Returns detailed information about a specific member (method, property, field, event) including parameters and documentation.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `typeFullName` | string | Yes | Fully-qualified type name |
| `memberName` | string | Yes | The member name (e.g., "SerializeObject") |
| `version` | string | No | Package version |
| `targetFramework` | string | No | Target framework moniker |

#### `search_types`
Searches for types by regex pattern.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `packageId` | string | Yes | The NuGet package ID |
| `pattern` | string | Yes | Regex pattern to match type names (e.g., ".*Converter$") |
| `version` | string | No | Package version |
| `targetFramework` | string | No | Target framework moniker |

## Example

![Example of NuGetAssemblyMcp in action](example-screenshot.png)

## Usage Examples

### Explore a Package

```
"What namespaces are in Newtonsoft.Json?"
→ list_namespaces(packageId: "Newtonsoft.Json")

"Show me all types in the Newtonsoft.Json.Linq namespace"
→ list_types(packageId: "Newtonsoft.Json", ns: "Newtonsoft.Json.Linq")
```

### Inspect Types

```
"Tell me about the JsonConvert class"
→ get_type_info(packageId: "Newtonsoft.Json", typeFullName: "Newtonsoft.Json.JsonConvert")

"What does SerializeObject do?"
→ get_member_info(packageId: "Newtonsoft.Json", typeFullName: "Newtonsoft.Json.JsonConvert", memberName: "SerializeObject")
```

### Search

```
"Find all converter types in Newtonsoft.Json"
→ search_types(packageId: "Newtonsoft.Json", pattern: ".*Converter$")
```

### Package Metadata

```
"Is this package deprecated or vulnerable?"
→ get_package_metadata(packageId: "Newtonsoft.Json")

"What license does Serilog use?"
→ get_package_metadata(packageId: "Serilog")
```

### Private Feeds

```
"Use my solution's NuGet sources"
→ set_working_directory(directoryPath: "/path/to/MySolution")
→ list_sources()

"Load a package from our private feed"
→ load_package(packageId: "MyCompany.Internal.Package")
```

### Version-Specific Queries

```
"What changed in version 12.0.0?"
→ load_package(packageId: "Newtonsoft.Json", version: "12.0.0")
→ list_types(packageId: "Newtonsoft.Json", version: "12.0.0")
```

## Architecture

```mermaid
flowchart TB
    subgraph Client
        MCP[MCP Client<br/>Claude Desktop, etc.]
    end

    subgraph NuGetAssemblyMcp
        subgraph Tools
            PT[PackageTools<br/>set_working_directory, list_package_versions,<br/>load_package, get_package_metadata, list_sources]
            AT[AssemblyTools<br/>list_namespaces, list_types, get_type_info, etc.]
        end
        
        subgraph Services
            NPS[NuGetPackageService<br/>Download & cache packages]
            AIS[AssemblyInspectionService<br/>Mono.Cecil reflection]
            XDS[XmlDocService<br/>XML documentation parsing]
            SLS[SourceLinkService<br/>PDB/SourceLink extraction]
        end
    end

    NuGet[(NuGet.org<br/>+ Private Feeds)]

    MCP <-->|stdio| Tools
    Tools --> Services
    NPS --> NuGet
```

## Cache

Packages are cached locally to avoid re-downloading:

```
~/.nuget-mcp/cache/
├── newtonsoft.json/
│   └── 13.0.3/
│       ├── lib/net8.0/
│       │   ├── Newtonsoft.Json.dll
│       │   ├── Newtonsoft.Json.xml
│       │   └── Newtonsoft.Json.pdb
│       └── Newtonsoft.Json.nuspec
```

The `.nupkg` file is deleted after extraction to save disk space.

## Dependencies

| Package | Purpose |
|---------|---------|
| [ModelContextProtocol](https://github.com/modelcontextprotocol/csharp-sdk) | MCP server framework |
| [Mono.Cecil](https://github.com/jbevain/cecil) | IL-level assembly inspection |
| [NuGet.Protocol](https://www.nuget.org/packages/NuGet.Protocol) | NuGet API client |
| [Microsoft.Extensions.Hosting](https://www.nuget.org/packages/Microsoft.Extensions.Hosting) | DI and hosting |

## Running Tests

```bash
dotnet test
```

Tests use [`Duende.IdentityServer`](https://www.nuget.org/packages/Duende.IdentityServer) as a real-world package fixture.

## License

MIT License — Copyright (c) 2026 Khalid Abuhakmeh
