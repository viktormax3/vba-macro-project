# Skill: FrxEdit VBA UserForm Builder (Advanced Agentic Reference)

You are equipped to autonomously create, design, and modify MS-Forms (`.frx` / `.frm`) UserForms without needing the VBA IDE. You interact with the Visual Basic application entirely through JSON structure files using the custom `frxedit` compiler built for this project.

## 1. Core Workflow & CLI Commands

**Never modify binary files (`.frx`) or complex positional logic in `.frm` directly.** Always interact via JSON patching using the following lifecycle:

| Action | Command | Purpose |
| :--- | :--- | :--- |
| **Inspect** | `frxedit inspect UserForm1.frm --as-patch --out layout.json` | Extracts the GUI layout into a flat JSON dictionary. `--as-patch` ignores default properties to keep the JSON concise for editing. |
| **Template** | `frxedit inspect UserForm1.frm --as-template --out template.json` | Extracts the full layout (including structure) for cloning or transferring to a new form. |
| **Build** | `frxedit build UserForm1.frm --patch layout.json --out UserForm1.frm` | Regenerates the OLE/CFB `.frx` container and `.frm` code-behind, merging your JSON layout changes back in. |
| **Watch** | `frxedit watch UserForm1.frm` | Runs continuously. Rebuilds the binary automatically in <250ms whenever the JSON patch, VBA code, or image assets change. |

## 2. Control Operations (DOM Manipulation)

The extracted `layout.json` has a standard flat dictionary structure. The system uses a declarative `$action` meta-property to manage the component lifecycle:
```json
{
  "properties": {
    "UserForm1": { "widthPt": 300, "heightPt": 400 },
    "CommandButton1": { 
      "$action": "edit", 
      "$newName": "", 
      "leftPt": 10, 
      "topPt": 10, 
      "widthPt": 100, 
      "heightPt": 25 
    }
  }
}
```

*   **Editing a Control:** This is the default. Properties are updated in place. If a control is omitted from the JSON, it is simply ignored and remains unchanged in the binary.
*   **Adding a Control:** Add a new key to the `properties` dictionary and explicitly set `"$action": "add"`. You **must** also provide the `"type"` (e.g., `"CommandButton"`) and the bounds: `leftPt`, `topPt`, `widthPt`, and `heightPt`.
*   **Removing a Control:** To delete a control from the binary, you **MUST NOT** simply delete the key from the JSON. Instead, set its `$action` property to `"remove"` (e.g., `"CommandButton1": { "$action": "remove" }`).
*   **Renaming a Control:** Do not change the dictionary key. Keep the original key name and set `"$action": "rename"` along with `"$newName": "YourNewName"`. In the next WYSIWYG sync, the dictionary key will be updated to the new name.
*   **VBA Event Handlers:** When adding, removing, or renaming controls, ensure you also update any associated VBA event handlers (e.g., `Private Sub TextBox2_Click()`) in the `.frm` text file to prevent compilation errors in Excel.

## 3. Data Types & Semantic Values

| Type | Format & Valid Values | Description |
| :--- | :--- | :--- |
| **Pt (Point)** | Decimal (e.g., `120.5`, `34`) | Used for ALL spatial metrics (`leftPt`, `widthPt`, `fontSize`). *Never use logical or twip units.* |
| **Color** | Hex: `"#RRGGBB"`<br>System: `"systemButtonFace"`, `"systemWindow"`, `"systemWindowText"`, `"systemHighlight"` | The compiler translates these to/from MS-Forms integer masks automatically. |
| **Boolean** | `true` or `false` | Standard JSON booleans. |
| **String** | Text (e.g., `"Submit"`) | Text values for captions, names, tags. |
| **Image Path** | Relative path: `"images/logo.jpg"` | Used for the `picture` or `mouseIcon` properties. The compiler handles the base64 binary encoding. |

## 4. Universal Container Properties

These properties apply to **UserForm** and **Frame** controls.
*(Note: For the UserForm root, properties are prefixed with `form`, e.g., `formCaption`, `formBackColor`, `formBorderStyle`).*

| Property | Type | Values/Enums |
| :--- | :--- | :--- |
| `caption` | String | Window/Frame title. |
| `backColor`, `foreColor`, `borderColor` | Color | Standard color types. |
| `borderStyle` | Integer | `0` (None), `1` (Single). |
| `specialEffect` | Integer | `0` (Flat), `1` (Raised), `2` (Sunken), `3` (Etched), `4` (Bump). |
| `scrollBars` | Integer | `0` (None), `1` (Horiz), `2` (Vert), `3` (Both). |
| `mousePointer` | Integer | `0` (Default), `1` (Arrow), `3` (IBeam), `11` (Hourglass), `99` (Custom). |
| `pictureSizeMode` | Integer | `0` (Clip), `1` (Stretch), `3` (Zoom). |
| `pictureAlignment` | Integer | `0` (TopLeft), `1` (TopRight), `2` (Center), `3` (BottomLeft), `4` (BottomRight). |
| `picture` | Img Path | Path to the background image. |

## 5. Standard Control Properties Catalog

**All visible controls REQUIRE:** `leftPt`, `topPt`, `widthPt`, `heightPt`.
**All interactable controls SUPPORT:** `enabled` (Bool), `locked` (Bool), `visible` (Bool), `tabIndex` (Int), `tabStop` (Bool), `tag` (String), `controlTipText` (String), `controlSource` (String).
**Typography Supported by ALL text controls:** `fontName` (String), `fontSize` (Pt), `fontWeight` (Int: `400`=Normal, `700`=Bold), `fontItalic` (Bool), `fontUnderline` (Bool), `fontStrikethrough` (Bool).

### CommandButton
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `caption` | String | Button text. |
| `backColor`, `foreColor` | Color | Background and text color. |
| `wordWrap`, `autoSize` | Bool | Text wrapping and auto-resizing. |
| `default`, `cancel` | Bool | Triggered by ENTER (`default`) or ESC (`cancel`). |
| `picturePosition` | Integer | `0` (LeftTop) to `12` (RightBottom). |
| `accelerator` | String | Shortcut key (single char). |

### Label
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `caption` | String | Label text. |
| `backColor`, `foreColor`, `borderColor` | Color | Visual colors. |
| `borderStyle`, `specialEffect`, `backStyle` | Integer | `backStyle`: `0` (Transparent), `1` (Opaque). |
| `wordWrap`, `autoSize` | Bool | Layout handling. |
| `textAlign` | Integer | `1` (Left), `2` (Center), `3` (Right). |

### TextBox
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `value` / `text` | String | Initial text content. |
| `backColor`, `foreColor`, `borderColor` | Color | Visual colors. |
| `borderStyle`, `specialEffect`, `backStyle` | Integer | Standard visual enumerations. |
| `multiLine`, `wordWrap`, `autoSize` | Bool | Support for multi-line text blocks. |
| `scrollBars` | Integer | `0` (None), `1` (Horiz), `2` (Vert), `3` (Both). |
| `maxLength` | Integer | Char limit (`0` = unlimited). |
| `passwordChar` | String | Char to mask input (e.g., `*`). |
| `textAlign` | Integer | `1` (Left), `2` (Center), `3` (Right). |
| `enterKeyBehavior` | Bool | `true`: ENTER creates new line (requires multiLine). |

### CheckBox / OptionButton / ToggleButton
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `value` | String | `"0"` (Unchecked), `"1"` (Checked), `"2"` (Mixed). |
| `caption` | String | Text description. |
| `groupName` | String | (OptionButton only) Groups radio buttons logically. |
| `backColor`, `foreColor` | Color | Visual colors. |
| `wordWrap`, `autoSize` | Bool | Layout handling. |
| `alignment` | Integer | Checkbox placement relative to text: `0` (Left), `1` (Right). |

### ComboBox / ListBox
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `value` / `text` | String | Selected value. |
| `rowSource` | String | Excel range linking (e.g., `"Sheet1!A1:A10"`). |
| `columnCount`, `boundColumn`, `textColumn`| Integer | Column configuration. |
| `listWidth` | Pt | Width of the dropdown menu. |
| `listRows` | Integer | (ComboBox only) Items to display before scrolling. |
| `matchEntry` | Integer | `0` (FirstLetter), `1` (Complete), `2` (None). |
| `listStyle` | Integer | `0` (Plain), `1` (Option - shows checkboxes). |
| `multiSelect` | Integer | (ListBox only) `0` (Single), `1` (Multi), `2` (Extended). |
| `showDropButtonWhen` | Integer | (ComboBox only) `0` (Never), `1` (Focus), `2` (Always). |

### ScrollBar / SpinButton
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `value` / `position` | Integer | Current value. |
| `min`, `max` | Integer | Value boundaries. |
| `smallChange`, `largeChange` | Integer | Increment steps (largeChange is ScrollBar only). |
| `orientation` | Integer | `-1` (Auto), `0` (Vertical), `1` (Horizontal). |
| `delay` | Integer | Auto-repeat delay in ms. |
| `proportionalThumb` | Bool | Thumb size reflects view ratio. |

### Image
| Property | Type | Description / Enums |
| :--- | :--- | :--- |
| `picture` | Img Path | Path to local image file. |
| `pictureSizeMode` | Integer | `0` (Clip), `1` (Stretch), `3` (Zoom). |
| `pictureAlignment` | Integer | `0` (TopLeft) through `4` (BottomRight). |
| `backColor`, `borderColor` | Color | Visual colors. |
| `borderStyle`, `specialEffect`, `backStyle` | Integer | Standard enumerations. |
