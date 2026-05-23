# Supported Controls & Properties

FrxEdit and its schema validation engine fully support the core 15 MS-Forms controls. The following diagrams define the supported properties, their expected JSON types, and valid values.

*Note: All controls implicitly support basic layout properties like `name` (string), `left` (number), `top` (number), `width` (number), `height` (number), `tabIndex` (integer), and `tabStop` (boolean).*

## Control Property Schemas

```mermaid
classDiagram
    class UserForm {
        +string caption
        +string backColor (Hex/System)
        +string foreColor (Hex/System)
        +boolean enabled
        +string fontName
        +number fontSize
        +boolean fontBold
        +boolean fontItalic
        +number pictureAlignment (0-4)
        +number pictureSizeMode (0-3)
        +boolean pictureTiling
        +number scrollBars (0-3)
        +boolean keepScrollBarsVisible
        +number scrollHeight
        +number scrollWidth
        +number scrollLeft
        +number scrollTop
        +boolean rightToLeft
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class CommandButton {
        +string caption
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean wordWrap
        +boolean autoSize
        +boolean default
        +boolean cancel
        +boolean takeFocusOnClick
        +string fontName
        +number fontSize
        +number picturePosition (0-12)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class TextBox {
        +string text
        +string value
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean autoSize
        +boolean autoWordSelect
        +boolean autoTab
        +boolean dragBehavior
        +boolean hideSelection
        +boolean multiLine
        +boolean wordWrap
        +boolean selectionMargin
        +number maxLength
        +string passwordChar
        +number textAlign (1-3)
        +number scrollBars (0-3)
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class Label {
        +string caption
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean autoSize
        +boolean wordWrap
        +string accelerator
        +number textAlign (1-3)
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class CheckBox {
        +string caption
        +string value (True/False/Null)
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean autoSize
        +boolean wordWrap
        +boolean tripleState
        +string accelerator
        +number textAlign (1-3)
        +number specialEffect (0-6)
        +number alignment (0-1)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class OptionButton {
        +string caption
        +string value (True/False/Null)
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean autoSize
        +boolean wordWrap
        +boolean tripleState
        +string accelerator
        +number textAlign (1-3)
        +number specialEffect (0-6)
        +number alignment (0-1)
        +string groupName
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class ToggleButton {
        +string caption
        +string value (True/False/Null)
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean autoSize
        +boolean wordWrap
        +boolean tripleState
        +string accelerator
        +number textAlign (1-3)
        +number picturePosition (0-12)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }
```

## Containers & Advanced Controls

```mermaid
classDiagram
    class Frame {
        +string caption
        +string backColor
        +string foreColor
        +boolean enabled
        +number scrollBars (0-3)
        +boolean keepScrollBarsVisible
        +number scrollHeight
        +number scrollWidth
        +number scrollLeft
        +number scrollTop
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class MultiPage {
        +string backColor
        +string foreColor
        +boolean enabled
        +number style (0-2)
        +number tabOrientation (0-3)
        +boolean multiRow
        +number value (Active Page Index)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }
    
    class Page {
        +string caption
        +boolean enabled
        +string accelerator
        +boolean transitionEffect
        +number transitionPeriod
    }
    
    class TabStrip {
        +string backColor
        +string foreColor
        +boolean enabled
        +number style (0-2)
        +number tabOrientation (0-3)
        +boolean multiRow
        +number value
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class ComboBox {
        +string text
        +string value
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +boolean autoSize
        +boolean autoWordSelect
        +boolean autoTab
        +boolean hideSelection
        +boolean selectionMargin
        +number maxLength
        +number textAlign (1-3)
        +number style (0-2)
        +number matchEntry (0-2)
        +number listRows
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class ListBox {
        +string value
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean locked
        +number textAlign (1-3)
        +number matchEntry (0-2)
        +number multiSelect (0-2)
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class ScrollBar {
        +number value
        +number min
        +number max
        +number smallChange
        +number largeChange
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean proportionalThumb
        +number orientation (0-1)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class SpinButton {
        +number value
        +number min
        +number max
        +number smallChange
        +string backColor
        +string foreColor
        +boolean enabled
        +number orientation (0-1)
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    class Image {
        +string backColor
        +string foreColor
        +boolean enabled
        +boolean autoSize
        +number pictureAlignment (0-4)
        +number pictureSizeMode (0-3)
        +boolean pictureTiling
        +number specialEffect (0-6)
        +number borderStyle (0-1)
        +string borderColor
        +number mousePointer (0-15, 99)
        +string mouseIcon (Base64/File)
    }

    MultiPage *-- Page : contains
```

## Enum Value References
For properties requiring an integer enum, FrxEdit uses the standard MS-Forms VBA constants exactly as defined by Microsoft. Use the integer values below in your JSON patches.

### fmPictureAlignment (`pictureAlignment`)
* `0`: fmPictureAlignmentTopLeft
* `1`: fmPictureAlignmentTopRight
* `2`: fmPictureAlignmentCenter
* `3`: fmPictureAlignmentBottomLeft
* `4`: fmPictureAlignmentBottomRight

### fmPictureSizeMode (`pictureSizeMode`)
* `0`: fmPictureSizeModeClip
* `1`: fmPictureSizeModeStretch
* `3`: fmPictureSizeModeZoom

### fmScrollBars (`scrollBars`)
* `0`: fmScrollBarsNone
* `1`: fmScrollBarsHorizontal
* `2`: fmScrollBarsVertical
* `3`: fmScrollBarsBoth

### fmPicturePosition (`picturePosition`)
* `0`: fmPicturePositionLeftTop
* `1`: fmPicturePositionLeftCenter
* `2`: fmPicturePositionLeftBottom
* `3`: fmPicturePositionRightTop
* `4`: fmPicturePositionRightCenter
* `5`: fmPicturePositionRightBottom
* `6`: fmPicturePositionAboveLeft
* `7`: fmPicturePositionAboveCenter
* `8`: fmPicturePositionAboveRight
* `9`: fmPicturePositionBelowLeft
* `10`: fmPicturePositionBelowCenter
* `11`: fmPicturePositionBelowRight
* `12`: fmPicturePositionCenter

### fmTextAlign (`textAlign`)
* `1`: fmTextAlignLeft
* `2`: fmTextAlignCenter
* `3`: fmTextAlignRight

### fmSpecialEffect (`specialEffect`)
* `0`: fmSpecialEffectFlat
* `1`: fmSpecialEffectRaised
* `2`: fmSpecialEffectSunken
* `3`: fmSpecialEffectEtched
* `6`: fmSpecialEffectBump

### fmBorderStyle (`borderStyle`)
* `0`: fmBorderStyleNone
* `1`: fmBorderStyleSingle

### fmAlignment (`alignment`)
* `0`: fmAlignmentLeft
* `1`: fmAlignmentRight

### fmTabStyle (`style`)
* `0`: fmTabStyleTabs
* `1`: fmTabStyleButtons
* `2`: fmTabStyleNone

### fmTabOrientation (`tabOrientation`)
* `0`: fmTabOrientationTop
* `1`: fmTabOrientationBottom
* `2`: fmTabOrientationLeft
* `3`: fmTabOrientationRight

### fmMatchEntry (`matchEntry`)
* `0`: fmMatchEntryFirstLetter
* `1`: fmMatchEntryComplete
* `2`: fmMatchEntryNone

### fmMultiSelect (`multiSelect`)
* `0`: fmMultiSelectSingle
* `1`: fmMultiSelectMulti
* `2`: fmMultiSelectExtended

### fmOrientation (`orientation`)
* `-1`: fmOrientationAuto
* `0`: fmOrientationVertical
* `1`: fmOrientationHorizontal

### fmMousePointer (`mousePointer`)
* `0`: fmMousePointerDefault
* `1`: fmMousePointerArrow
* `2`: fmMousePointerCross
* `3`: fmMousePointerIBeam
* `6`: fmMousePointerNESW
* `7`: fmMousePointerNS
* `8`: fmMousePointerNWSE
* `9`: fmMousePointerWE
* `10`: fmMousePointerUpArrow
* `11`: fmMousePointerHourGlass
* `12`: fmMousePointerNoDrop
* `13`: fmMousePointerAppStarting
* `14`: fmMousePointerHelp
* `15`: fmMousePointerSizeAll
* `99`: fmMousePointerCustom

## Color Properties

Properties like `backColor`, `foreColor`, and `borderColor` accept three different formats:

1. **Web Hex Format**: The standard web format `"#RRGGBB"` (e.g., `"#FF0000"` for pure red). FrxEdit automatically translates this to the internal MS-Forms `0x00BBGGRR` format.
2. **Legacy VBA Format**: The exact VBA hex format `"&H00BBGGRR&"`.
3. **System Colors**: A literal string representing a native OS system color.

### Supported System Colors
The following literal strings can be used to assign dynamic OS UI colors:
* `"systemScrollbar"`
* `"systemBackground"`
* `"systemActiveCaption"`
* `"systemInactiveCaption"`
* `"systemMenu"`
* `"systemWindow"`
* `"systemWindowFrame"`
* `"systemMenuText"`
* `"systemWindowText"`
* `"systemCaptionText"`
* `"systemActiveBorder"`
* `"systemInactiveBorder"`
* `"systemAppWorkspace"`
* `"systemHighlight"`
* `"systemHighlightText"`
* `"systemButtonFace"`
* `"systemButtonShadow"`
* `"systemGrayText"`
* `"systemButtonText"`
* `"systemInactiveCaptionText"`
* `"systemButtonHighlight"`
* `"system3DDarkShadow"`
* `"system3DLight"`
* `"systemInfoText"`
* `"systemInfoBackground"`
