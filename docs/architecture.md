# FrxEdit Architecture & Binary Format

This document provides a technical overview of how FrxEdit interacts with Microsoft Forms (MS-OFORMS) binary streams and text layouts to achieve perfect binary parity.

## The Dual-File Structure
A standard VBA form consists of two synchronized files:
1.  **`.frm` (Text Layout)**: A legacy VB6-style plain text file. It stores the macro code, high-level control declarations (e.g., `Begin MSForms.CommandButton`), and absolute positioning in **Points** (pt).
2.  **`.frx` (Binary Storage)**: An **OLE Compound File Binary Format (CFB)**. It acts as a miniature file system inside a single file, storing rich streams of binary data that plain text cannot represent natively, such as:
    *   Embedded Images (`.frx` offsets).
    *   OLE control metadata (`f`, `o`, `x` streams).
    *   Complex Unicode strings that cannot be saved in the `.frm` ANSI encoding.
    *   Typographic arrays (StdFont variants).

## OLE Streams (`f`, `o`, `x`)
Within the `.frx` CFB file, each control (including the root UserForm) is assigned a "Site" containing three critical binary streams:

*   **`f` (Form Data)**: Contains the intrinsic properties of the control (Site layout, flags). For the root UserForm, this dictates the global alignment, grid states, and macro storage pointers.
*   **`o` (Object Data)**: The primary payload. It contains the control's specific properties serialized as binary structs (e.g., BackColor, Caption, Enabled, MousePointer).
*   **`x` (Extended Data)**: Stores dynamically sized properties like MultiPage arrays, TabStrip tabs, and extended strings.

## The FrxEdit Rebuild Pipeline
FrxEdit achieves perfect binary manipulation through a strict pipeline:

1.  **JSON Patch DOM (`PatchDocument`)**: When a user or an AI Agent runs `frxedit build`, FrxEdit reads the target `patch.json`. This JSON document represents the state of the UI (modifications, creations, deletions).
2.  **Schema Validation**: The patch is strictly validated against `MsFormsControlSchemaCatalog`. This prevents the injection of illegal types (e.g., placing a string in a boolean property), which would cause a fatal crash in the host VBA environment (Excel/Corel).
3.  **Round-Trip Parsing**: 
    *   FrxEdit opens the original `.frx` and reads the CFB streams using its own **custom, zero-dependency CFB parser** (`CompoundStorageInspector`).
    *   It parses the existing `f`, `o`, and `x` streams into in-memory .NET Objects (`LocatedValue`).
4.  **In-Place Morphing**: 
    *   The `PatchApplier` iterates through the JSON patch.
    *   Instead of creating new streams from scratch (which breaks MS-OFORMS parity), FrxEdit **morphs** the existing .NET objects in memory.
    *   It updates twips, hex colors, font flags, and byte-alignments dynamically.
5.  **CFB Serialization**: 
    *   The morphed objects are serialized back into byte streams.
    *   A completely new `.frx` CFB container is generated to avoid OLE fragmentation (a common issue when manipulating `.frx` files directly).
6.  **Text Layout Generation**: 
    *   Finally, FrxEdit generates the new `.frm` text layout, recalculating the `pt` (points) dimensions from the `twips` stored in the binary, ensuring the VBA Editor opens it seamlessly.

## AI Design Contracts
By abstracting the complex CFB binary logic into a clean JSON interface, FrxEdit allows LLMs to design UIs. The AI does not need to know about `twips`, OLE headers, or `Site` allocations; it only outputs JSON according to our schema, and FrxEdit handles the binary compilation.
