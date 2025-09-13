# DLL Information Display in Vibe.Gui

## Goals

When a library is selected in the tree view, Vibe.Gui should expose details that help analysts understand how the DLL behaves and how it relates to the rest of the application.  The panel should be fast to populate and divide information into sections for quick scanning.

## Shared properties

These fields apply to both managed and unmanaged libraries:

- File name and on-disk path
- File size and timestamp
- CPU architecture (x86, x64, ARM, AnyCPU)
- File and product version information
- Company and description from the PE version resource
- Digital signature status and certificate details
- Cryptographic hashes (SHA-256, SHA-1, MD5)
- Build timestamp and linker checksum

## Managed assemblies

In addition to the shared fields, managed (.NET) assemblies can surface:

- Assembly identity (name, version, culture, public key token)
- Strong-name signature state
- Target framework and build configuration
- Entry point method
- Referenced assemblies and NuGet package origin where available
- Counts of namespaces, types and methods
- Custom attributes and security declarations
- Embedded resources and manifest metadata
- Debug information such as PDB path
- Preview pane for decompiled C# code

## Unmanaged libraries

Native DLLs have a different set of interesting characteristics:

- Machine type and subsystem
- Exported functions with ordinals and decorated names
- Imported modules and API usage statistics
- Size and permissions of each PE section
- Relocation and exception directory summaries
- Debug data including CodeView and symbol server hints
- Version resources and type library information

## User interaction

The details view can provide:

- Collapsible sections for general, managed and native data
- Search and copy-to-clipboard actions for tables
- Links to open dependencies or jump to exported functions
- Indicators for missing dependencies or signature problems

The goal is to offer a quick, information-dense snapshot of a DLL without requiring the user to open multiple separate tools.
