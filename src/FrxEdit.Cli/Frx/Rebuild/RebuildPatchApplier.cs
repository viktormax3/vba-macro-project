internal static class RebuildPatchApplier
{
    private static readonly HashSet<string> ObjectPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "caption",
        "value",
        "groupName",
        "fontName",
        "fontSize",
        "backColor",
        "foreColor",
        "borderColor",
        "enabled",
        "locked",
        "backStyle",
        "wordWrap",
        "autoSize",
        "imeMode",
        "picturePosition",
        "mousePointer",
        "accelerator",
        "alignment",
        "takeFocusOnClick",
        "borderStyle",
        "specialEffect",
        "textAlign",
        "paragraphAlign",
        "maxLength",
        "passwordChar",
        "scrollBars",
        "displayStyle",
        "listWidth",
        "boundColumn",
        "textColumn",
        "columnCount",
        "listRows",
        "matchEntry",
        "listStyle",
        "showDropButtonWhen",
        "dropButtonStyle",
        "multiSelect",
        "dragBehavior",
        "enterFieldBehavior",
        "enterKeyBehavior",
        "tabKeyBehavior",
        "selectionMargin",
        "autoWordSelect",
        "hideSelection",
        "autoTab",
        "multiLine",
        "integralHeight",
        "columnHeads",
        "matchRequired",
        "editable"
    };

    private static readonly HashSet<string> FormSitePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tabIndex",
        "controlTipText",
        "controlSource",
        "tabStop",
        "visible",
        "default",
        "cancel"
    };

    private static readonly HashSet<string> RootFormPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "formBackColor",
        "formForeColor",
        "formBorderColor",
        "formCaption",
        "formBorderStyle",
        "formMousePointer",
        "formScrollBars",
        "formCycle",
        "formSpecialEffect",
        "formPictureAlignment",
        "formPictureSizeMode",
        "formZoom",
        "nextAvailableId",
        "displayedWidth",
        "displayedHeight",
        "displayedWidthPt",
        "displayedHeightPt",
        "logicalWidth",
        "logicalHeight",
        "logicalWidthPt",
        "logicalHeightPt",
        "scrollLeft",
        "scrollTop"
    };

    public static LayoutInspection ApplyObjectPropertyPatch(LayoutInspection source, PatchDocument patch, bool allowFormSitePatch = false, string? formName = null, string? patchDir = null)
    {
        ValidateObjectPatch(patch, allowFormSitePatch, formName);

        if ((patch.Properties is null || patch.Properties.Count == 0) &&
            (!allowFormSitePatch || patch.Layout is null || patch.Layout.Count == 0) &&
            (!allowFormSitePatch || patch.Renames is null || patch.Renames.Count == 0) &&
            (!allowFormSitePatch || patch.Move is null || patch.Move.Count == 0) &&
            (!allowFormSitePatch || patch.Add is null || patch.Add.Count == 0) &&
            (!allowFormSitePatch || patch.Remove is null || patch.Remove.Count == 0))
        {
            return source;
        }

        var patchedByName = patch.Properties?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase);

        var frxFormControl = source.FrxFormControl;
        if (frxFormControl is not null)
        {
            var formKeys = new[] { formName, "UserForm", "Form", "root" };
            foreach (var key in formKeys)
            {
                if (key is not null && patchedByName.TryGetValue(key, out var formPatches))
                {
                    var newFrxFormControl = new Dictionary<string, object?>(frxFormControl, StringComparer.OrdinalIgnoreCase);
                    foreach (var (propName, propVal) in formPatches)
                    {
                        ApplyFormPropertyToDictionary(key, newFrxFormControl, propName, propVal, patchDir);
                    }
                    frxFormControl = newFrxFormControl;
                    break;
                }
            }
        }

        var layoutByName = allowFormSitePatch
            ? patch.Layout?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LayoutPatch>(StringComparer.OrdinalIgnoreCase);

        var renameByName = allowFormSitePatch
            ? patch.Renames?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var moveByName = allowFormSitePatch
            ? patch.Move?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var removeRequests = allowFormSitePatch
            ? (patch.Remove ?? []).Concat(moveByName.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var removalPlan = allowFormSitePatch
            ? BuildRemovalPlan(source.Controls, removeRequests)
            : new RemovalPlan(new HashSet<string>(StringComparer.OrdinalIgnoreCase), []);

        var removedControls = new List<ControlInfo>();
        var controls = new List<ControlInfo>(source.Controls.Count + (patch.Add?.Count ?? 0));
        foreach (var control in source.Controls)
        {
            if (removalPlan.ControlNames.Contains(control.Name))
            {
                if (removeRequests.Contains(control.Name))
                {
                    ValidateRemovedControl(source.Controls, control);
                }

                removedControls.Add(control);
                continue;
            }

            renameByName.TryGetValue(control.Name, out var newName);
            patchedByName.TryGetValue(control.Name, out var requested);
            layoutByName.TryGetValue(control.Name, out var layout);

            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (requested is null)
                {
                    patchedByName.TryGetValue(newName, out requested);
                }

                if (layout is null)
                {
                    layoutByName.TryGetValue(newName, out layout);
                }
            }

            if (requested is null && layout is null && string.IsNullOrWhiteSpace(newName))
            {
                controls.Add(control);
                continue;
            }

            controls.Add(ApplyToControl(control, requested, layout, newName, patchDir));
        }

        if (renameByName.Count > 0)
        {
            controls = controls
                .Select(control => control.Parent is not null && renameByName.TryGetValue(control.Parent, out var renamedParent)
                    ? control with { Parent = renamedParent }
                    : control)
                .ToList();
        }

        if (allowFormSitePatch && moveByName.Count > 0)
        {
            controls.AddRange(BuildMovedControls(source.Controls, controls, moveByName, patchedByName, layoutByName, patchDir));
        }

        if (allowFormSitePatch && patch.Add is { Count: > 0 })
        {
            controls.AddRange(BuildAddedControls(source.Controls, controls, patch.Add, patchDir));
        }

        return source with { Controls = controls, RemovedControls = removedControls, RemovedStoragePaths = removalPlan.StoragePaths, FrxFormControl = frxFormControl };
    }

    public static void ValidateObjectPatch(PatchDocument patch, bool allowFormSitePatch = false, string? formName = null)
    {
        if (patch.Add is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'add' because new controls require FormSiteData rebuild. Use '--stream-mode full-patch'.");
        }

        if (patch.Move is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'move' because moving controls requires FormSiteData rebuild. Use '--stream-mode full-patch'.");
        }

        if (patch.Remove is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'remove' because removing controls requires FormSiteData rebuild. Use '--stream-mode full-patch'.");
        }

        if (patch.Renames is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'renames' because control names live in FormSiteData. Use '--stream-mode full-patch' for rebuild renames.");
        }

        if (patch.Layout is { Count: > 0 } && !allowFormSitePatch)
        {
            throw new CliException("Rebuild object-patch does not support 'layout'. Use '--stream-mode full-patch' for rebuild layout edits.");
        }

        foreach (var (controlName, properties) in patch.Properties ?? new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.OrdinalIgnoreCase))
        {
            var isForm = (formName is not null && string.Equals(controlName, formName, StringComparison.OrdinalIgnoreCase)) ||
                         string.Equals(controlName, "UserForm", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(controlName, "Form", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(controlName, "root", StringComparison.OrdinalIgnoreCase);

            foreach (var propertyName in properties.Keys)
            {
                if (isForm)
                {
                    if (!RootFormPropertyNames.Contains(propertyName))
                    {
                        continue;
                    }
                }
                else
                {
                    if (!ObjectPropertyNames.Contains(propertyName) && !(allowFormSitePatch && FormSitePropertyNames.Contains(propertyName)))
                    {
                        continue;
                    }
                }
            }
        }

        foreach (var name in patch.Remove ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CliException("Each remove entry requires a non-empty control name.");
            }
        }

        foreach (var (name, _) in patch.Move ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CliException("Each move entry requires a non-empty source control name.");
            }
        }

        foreach (var add in patch.Add ?? [])
        {
            if (string.IsNullOrWhiteSpace(add.Name))
            {
                throw new CliException("Each add entry requires a non-empty 'name'.");
            }

            if (string.IsNullOrWhiteSpace(add.FromTemplate) && string.IsNullOrWhiteSpace(add.Type))
            {
                throw new CliException($"Add entry '{add.Name}' requires either 'fromTemplate' or 'type'.");
            }

            if (string.IsNullOrWhiteSpace(add.FromTemplate) &&
                !GeneratedControlFactory.CanCreate(add.Type!) &&
                !GeneratedStorageFactory.CanCreate(add.Type!))
            {
                throw new CliException($"Add entry '{add.Name}' requested type '{add.Type}', but this build can create only: {GeneratedControlFactory.SupportedTypes}, Frame, MultiPage, Page. Use 'fromTemplate' for other types.");
            }
        }
    }

    private sealed record RemovalPlan(HashSet<string> ControlNames, IReadOnlyList<string> StoragePaths);

    private static RemovalPlan BuildRemovalPlan(IReadOnlyList<ControlInfo> controls, HashSet<string> requestedNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var storagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (requestedNames.Count == 0)
        {
            return new RemovalPlan(names, []);
        }

        var byName = controls.ToDictionary(control => control.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var requested in requestedNames)
        {
            if (!byName.TryGetValue(requested, out var root))
            {
                throw new CliException($"Cannot remove '{requested}': control does not exist.");
            }

            CollectSubtree(root, controls, names);
            if (TryGetOwnedStoragePath(root, controls, out var storagePath))
            {
                storagePaths.Add(storagePath);
            }
        }

        return new RemovalPlan(names, storagePaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void CollectSubtree(ControlInfo root, IReadOnlyList<ControlInfo> controls, HashSet<string> names)
    {
        if (!names.Add(root.Name))
        {
            return;
        }

        foreach (var child in controls.Where(candidate => string.Equals(candidate.Parent, root.Name, StringComparison.OrdinalIgnoreCase)))
        {
            CollectSubtree(child, controls, names);
        }
    }

    private static void ValidateRemovedControl(IReadOnlyList<ControlInfo> controls, ControlInfo control)
    {
        if (control.Properties is null ||
            !TryGetString(control.Properties, "siteParser", out var siteParser) ||
            !siteParser.Equals("msOFormsOleSiteConcrete", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Cannot remove '{control.Name}': the control does not expose a documented OleSiteConcrete site.");
        }

        var hasChildren = controls.Any(candidate => string.Equals(candidate.Parent, control.Name, StringComparison.OrdinalIgnoreCase));
        if (!hasChildren)
        {
            if (TryGetInt(control.Properties, "objectStreamSize", out var objectStreamSize) && objectStreamSize > 0)
            {
                return;
            }

            if (IsStorageParentType(control.Type))
            {
                if (!TryGetOwnedStoragePath(control, controls, out _))
                {
                    throw new CliException($"Cannot remove '{control.Name}': could not determine the owned storage path to remove.");
                }

                EnsurePageRemovalLeavesSibling(control, controls);
                return;
            }

            throw new CliException($"Cannot remove '{control.Name}': this pass supports leaf object-stream controls, complete Frame/MultiPage containers, and Page containers.");
        }

        if (!IsStorageParentType(control.Type))
        {
            throw new CliException($"Cannot remove '{control.Name}': removing this parent type is not supported yet. This pass supports leaf controls and complete Frame/Page/MultiPage subtree removal.");
        }

        if (!TryGetOwnedStoragePath(control, controls, out _))
        {
            throw new CliException($"Cannot remove '{control.Name}': could not determine the owned storage path to remove.");
        }

        EnsurePageRemovalLeavesSibling(control, controls);
    }


    private static void EnsurePageRemovalLeavesSibling(ControlInfo control, IReadOnlyList<ControlInfo> controls)
    {
        if (!control.Type.Equals("Page", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(control.Parent))
        {
            throw new CliException($"Cannot remove page '{control.Name}': page does not expose a MultiPage parent.");
        }

        var siblingPageCount = controls.Count(candidate =>
            candidate.Type.Equals("Page", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Parent, control.Parent, StringComparison.OrdinalIgnoreCase) &&
            !candidate.Name.Equals(control.Name, StringComparison.OrdinalIgnoreCase));

        if (siblingPageCount <= 0)
        {
            throw new CliException($"Cannot remove page '{control.Name}': removing the last page of MultiPage '{control.Parent}' is not supported yet.");
        }
    }

    private static bool IsStorageParentType(string type) =>
        type.Equals("Frame", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Page", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetOwnedStoragePath(ControlInfo parent, IReadOnlyList<ControlInfo> controls, out string storagePath)
    {
        storagePath = string.Empty;

        var child = controls.FirstOrDefault(candidate => string.Equals(candidate.Parent, parent.Name, StringComparison.OrdinalIgnoreCase) &&
            candidate.Properties is not null &&
            TryGetString(candidate.Properties, "storagePath", out var childStoragePath) &&
            !string.IsNullOrWhiteSpace(childStoragePath));
        if (child?.Properties is not null && TryGetString(child.Properties, "storagePath", out storagePath) && !string.IsNullOrWhiteSpace(storagePath))
        {
            return true;
        }

        if (parent.Properties is not null &&
            TryGetString(parent.Properties, "generatedStoragePath", out var generatedStoragePath) &&
            !string.IsNullOrWhiteSpace(generatedStoragePath))
        {
            storagePath = generatedStoragePath;
            return true;
        }

        if (parent.Properties is null || !TryGetString(parent.Properties, "storagePath", out var owningStoragePath) || string.IsNullOrWhiteSpace(owningStoragePath))
        {
            return false;
        }

        if (!TryGetInt(parent.Properties, "siteId", out var id) && !TryGetInt(parent.Properties, "id", out id))
        {
            return false;
        }

        storagePath = $"{owningStoragePath}/i{FormatStorageId(id)}";
        return true;
    }

    private static string ResolveTargetStoragePath(string? parent, IReadOnlyList<ControlInfo> controls)
    {
        if (string.IsNullOrWhiteSpace(parent))
        {
            return "Root Entry";
        }

        var parentControl = controls.FirstOrDefault(c => c.Name.Equals(parent, StringComparison.OrdinalIgnoreCase))
            ?? throw new CliException($"Target parent '{parent}' does not exist.");

        if (parentControl.Type.Equals("TabStrip", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Target parent '{parent}' is a TabStrip. TabStrip is a selector, not a child-control container; use sibling Frame panels plus code.tabStripPanels or use MultiPage.");
        }

        if (!IsStorageParentType(parentControl.Type) || parentControl.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Target parent '{parent}' is type '{parentControl.Type}'. This pass supports adding/moving common controls into root, Frame, or Page containers only.");
        }

        if (!TryGetOwnedStoragePath(parentControl, controls, out var storagePath))
        {
            throw new CliException($"Target parent '{parent}' does not expose an owned storage path.");
        }

        return storagePath;
    }

    private static string ResolveTargetMultiPageStoragePath(string controlName, ControlInfo? parentControl)
    {
        if (parentControl is null)
        {
            throw new CliException($"Add target '{controlName}' is type Page and requires a MultiPage parent.");
        }

        if (!parentControl.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase))
        {
            throw new CliException($"Add target '{controlName}' is type Page, but parent '{parentControl.Name}' is type '{parentControl.Type}'. Page controls can only be added to a MultiPage.");
        }

        if (!TryGetOwnedStoragePath(parentControl, [parentControl], out var storagePath))
        {
            if (parentControl.Properties is not null &&
                TryGetString(parentControl.Properties, "storagePath", out var ownerStoragePath) &&
                TryGetInt(parentControl.Properties, "siteId", out var siteId))
            {
                storagePath = $"{ownerStoragePath}/i{FormatStorageId(siteId)}";
                return storagePath;
            }

            throw new CliException($"Add target '{controlName}' cannot determine storage path for MultiPage parent '{parentControl.Name}'.");
        }

        return storagePath;
    }

    private static string FormatStorageId(int id) => id is >= 0 and < 10 ? $"0{id}" : id.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<ControlInfo> BuildMovedControls(
        IReadOnlyList<ControlInfo> templateControls,
        IReadOnlyList<ControlInfo> existingControls,
        Dictionary<string, string?> moveByName,
        Dictionary<string, Dictionary<string, JsonElement>> patchedByName,
        Dictionary<string, LayoutPatch> layoutByName,
        string? patchDir)
    {
        var additions = new List<AddControlPatch>();
        foreach (var (controlName, newParent) in moveByName)
        {
            var template = templateControls.FirstOrDefault(c => c.Name.Equals(controlName, StringComparison.OrdinalIgnoreCase))
                ?? throw new CliException($"Cannot move '{controlName}': control does not exist.");

            if (templateControls.Any(candidate => string.Equals(candidate.Parent, template.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new CliException($"Cannot move '{controlName}': pass 36 only supports moving leaf object-stream controls. Move/remove containers separately.");
            }

            var add = new AddControlPatch
            {
                FromTemplate = controlName,
                Name = controlName,
                // In a move map, null/empty target means move to the root container.
                Parent = string.IsNullOrWhiteSpace(newParent) ? string.Empty : newParent.Trim(),
                Properties = patchedByName.TryGetValue(controlName, out var requested) ? requested : null
            };

            if (layoutByName.TryGetValue(controlName, out var layout))
            {
                add.Left = layout.Left;
                add.Top = layout.Top;
                add.RawWidth = layout.RawWidth ?? layout.Width;
                add.RawHeight = layout.RawHeight ?? layout.Height;
                add.LeftPt = layout.LeftPt;
                add.TopPt = layout.TopPt;
                add.WidthPt = layout.WidthPt;
                add.HeightPt = layout.HeightPt;
            }

            additions.Add(add);
        }

        return BuildAddedControls(templateControls, existingControls, additions, patchDir);
    }

    private static IEnumerable<ControlInfo> BuildAddedControls(IReadOnlyList<ControlInfo> templateControls, IReadOnlyList<ControlInfo> existingControls, IReadOnlyList<AddControlPatch> additions, string? patchDir)
    {
        var names = existingControls.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var maxId = templateControls.Concat(existingControls)
            .Select(c => c.Properties is not null && TryGetInt(c.Properties, "siteId", out var id) ? id : 0)
            .DefaultIfEmpty(0)
            .Max();

        var result = new List<ControlInfo>();
        foreach (var add in additions)
        {
            var name = add.Name!.Trim();
            if (!names.Add(name))
            {
                throw new CliException($"Add target '{name}' would duplicate an existing control.");
            }

            var hasTemplate = !string.IsNullOrWhiteSpace(add.FromTemplate);
            var template = hasTemplate
                ? templateControls.FirstOrDefault(c => c.Name.Equals(add.FromTemplate!, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"Add template '{add.FromTemplate}' does not exist.")
                : null;

            var type = add.Type?.Trim();
            if (template is not null)
            {
                type ??= template.Type;
                if (!template.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException($"Add target '{name}' requested type '{type}', but template '{template.Name}' is type '{template.Type}'. Template clones must keep the same type.");
                }
            }
            else if (string.IsNullOrWhiteSpace(type))
            {
                throw new CliException($"Add target '{name}' requires 'type' when no fromTemplate is supplied.");
            }

            var parent = add.Parent is null ? template?.Parent : (string.IsNullOrWhiteSpace(add.Parent) ? null : add.Parent.Trim());
            var controlsForParent = existingControls.Concat(result).ToList();
            ControlInfo? parentControl = null;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                parentControl = controlsForParent.FirstOrDefault(c => c.Name.Equals(parent, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CliException($"Target parent '{parent}' does not exist.");
            }

            var targetStoragePath = template is null && type!.Equals("Page", StringComparison.OrdinalIgnoreCase)
                ? ResolveTargetMultiPageStoragePath(name, parentControl)
                : ResolveTargetStoragePath(parent, controlsForParent);
            var targetStreamPath = $"{targetStoragePath}/f";

            if (template is not null && template.Properties is null)
            {
                throw new CliException($"Add template '{template.Name}' has no structured metadata.");
            }

            var props = template?.Properties is not null
                ? new Dictionary<string, object?>(template.Properties, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            maxId++;
            props["isAddedControl"] = true;
            if (template is not null)
            {
                if (!TryGetString(template.Properties!, "storagePath", out var templateStoragePath) || string.IsNullOrWhiteSpace(templateStoragePath) ||
                    !TryGetString(template.Properties!, "streamPath", out var templateStreamPath) || string.IsNullOrWhiteSpace(templateStreamPath))
                {
                    throw new CliException($"Add template '{template.Name}' is missing storagePath/streamPath metadata.");
                }

                props["templateControlName"] = template.Name;
                props["templateStoragePath"] = templateStoragePath;
                props["templateStreamPath"] = templateStreamPath;
                props["templateObjectStreamPath"] = $"{templateStoragePath}/o";
            }
            props["storagePath"] = targetStoragePath;
            props["streamPath"] = targetStreamPath;
            props["name"] = name;
            props["nameRaw"] = name;
            props["siteName"] = name;
            props["siteId"] = maxId;
            props["id"] = maxId;
            props["tabIndex"] = add.Properties is not null && add.Properties.TryGetValue("tabIndex", out var tabIndexElement)
                ? RequireUInt16(name, "tabIndex", tabIndexElement)
                : NextTabIndexForParent(existingControls.Concat(result), parent);

            if (!string.IsNullOrWhiteSpace(add.Caption))
            {
                props["caption"] = add.Caption;
            }

            if (!string.IsNullOrWhiteSpace(add.Value))
            {
                props["value"] = add.Value;
            }

            foreach (var (propertyName, value) in add.Properties ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
            {
                ApplyAddPropertyToDictionary(name, props, propertyName, value, patchDir);
            }

            var left = add.Left ?? ToRawPoints(add.LeftPt) ?? template?.Left ?? 0;
            var top = add.Top ?? ToRawPoints(add.TopPt) ?? template?.Top ?? 0;
            var rawWidth = add.RawWidth ?? add.Width ?? ToRawPoints(add.WidthPt) ?? template?.RawWidth;
            var rawHeight = add.RawHeight ?? add.Height ?? ToRawPoints(add.HeightPt) ?? template?.RawHeight;

            if (template is null && type.Equals("Page", StringComparison.OrdinalIgnoreCase))
            {
                if (parentControl is null || !parentControl.Type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CliException($"Add target '{name}' is type Page and must use an existing MultiPage as parent.");
                }

                var pageIndex = NextMultiPagePageIndex(existingControls.Concat(result), parentControl.Name);
                var generatedStoragePath = $"{targetStoragePath}/i{FormatStorageId(maxId)}";
                rawWidth ??= parentControl.RawWidth ?? 0;
                rawHeight ??= parentControl.RawHeight ?? 0;
                left = 0;
                top = 0;
                var pageCaption = TryGetString(props, "caption", out var captionValue) && !string.IsNullOrWhiteSpace(captionValue)
                    ? captionValue
                    : name;
                props["tabCaption"] = pageCaption;
                props.Remove("caption");
                props["tabIndex"] = pageIndex;
                var generated = GeneratedStorageFactory.CreatePage(
                    name,
                    maxId,
                    pageIndex,
                    rawWidth ?? 0,
                    rawHeight ?? 0,
                    generatedStoragePath,
                    pageIndex == 0);

                props["generatedFormSitePayload"] = generated.SitePayload;
                props["siteDepth"] = 0;
                props["siteType"] = 1;
                props["siteLocalOffset"] = 0;
                props["cbSite"] = generated.SitePayload.Length - 4;
                props["parser"] = "msOFormsFormSiteData";
                props["siteParser"] = "msOFormsOleSiteConcrete";
                props["siteBitFlags"] = pageIndex == 0 ? "0x00040021" : "0x00040023";
                props["formControlParser"] = "msOFormsFormControl";
                props["formPropMask"] = "0x0C000C48";
                props["sizeSource"] = "formControlDisplayedSize";
                props["displayedWidth"] = rawWidth ?? 0;
                props["displayedHeight"] = rawHeight ?? 0;
                props["logicalWidth"] = 0;
                props["logicalHeight"] = 0;
                props["generatedStoragePath"] = generated.StoragePath;
                props["generatedStorageF"] = generated.FStream;
                props["generatedStorageO"] = generated.OStream;
                props["generatedStorageCompObjKind"] = "Page";
                props["generatedPageProperties"] = generated.PageProperties;
                props["multiPageParent"] = parentControl.Name;
                props["multiPagePageIndex"] = pageIndex;
                props["multiPagePageId"] = generated.SiteId;
                props["multiPageXStreamPath"] = $"{targetStoragePath}/x";
            }
            else if (template is null && type.Equals("Frame", StringComparison.OrdinalIgnoreCase))
            {
                var ownedStoragePath = $"{targetStoragePath}/i{FormatStorageId(maxId)}";
                var generated = GeneratedStorageFactory.CreateFrame(
                    name,
                    maxId,
                    (int)props["tabIndex"]!,
                    left,
                    top,
                    rawWidth ?? 0,
                    rawHeight ?? 0,
                    add.Caption,
                    ownedStoragePath);

                props["generatedFormSitePayload"] = generated.SitePayload;
                props.Remove("caption");
                props["siteDepth"] = 0;
                props["siteType"] = 1;
                props["siteLocalOffset"] = 0;
                props["cbSite"] = generated.SitePayload.Length - 4;
                foreach (var (propertyName, propertyValue) in generated.Metadata)
                {
                    props[propertyName] = propertyValue;
                }
            }
            else if (template is null && type.Equals("MultiPage", StringComparison.OrdinalIgnoreCase))
            {
                var ownedStoragePath = $"{targetStoragePath}/i{FormatStorageId(maxId)}";
                var pageNames = MsFormsFactoryBinary.GetStringList(props, "pageNames")?.ToArray()
                    ?? [$"{name}Page1", $"{name}Page2"];
                var pageCaptions = MsFormsFactoryBinary.GetStringList(props, "pageCaptions")?.ToArray()
                    ?? pageNames.Select((_, index) => $"Page{index + 1}").ToArray();
                if (pageNames.Length != pageCaptions.Length || pageNames.Length == 0)
                {
                    throw new CliException($"Add target '{name}' has invalid pageNames/pageCaptions.");
                }

                foreach (var pageName in pageNames)
                {
                    if (!names.Add(pageName))
                    {
                        throw new CliException($"Add target '{name}' would create duplicate page '{pageName}'.");
                    }
                }

                var generated = GeneratedStorageFactory.CreateMultiPage(
                    name,
                    maxId,
                    (int)props["tabIndex"]!,
                    left,
                    top,
                    rawWidth ?? 0,
                    rawHeight ?? 0,
                    ownedStoragePath,
                    pageNames,
                    pageCaptions);

                props["generatedFormSitePayload"] = generated.SitePayload;
                props.Remove("caption");
                props["siteDepth"] = 0;
                props["siteType"] = 1;
                props["siteLocalOffset"] = 0;
                props["cbSite"] = generated.SitePayload.Length - 4;
                foreach (var (propertyName, propertyValue) in generated.Metadata)
                {
                    props[propertyName] = propertyValue;
                }

                for (var i = 0; i < generated.Pages.Count; i++)
                {
                    var page = generated.Pages[i];
                    var pageProps = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["isAddedControl"] = true,
                        ["storagePath"] = ownedStoragePath,
                        ["streamPath"] = $"{ownedStoragePath}/f",
                        ["name"] = page.Name,
                        ["nameRaw"] = page.Name,
                        ["siteName"] = page.Name,
                        ["siteId"] = page.SiteId,
                        ["id"] = page.SiteId,
                        ["tabIndex"] = i,
                        ["parser"] = "msOFormsFormSiteData",
                        ["siteParser"] = "msOFormsOleSiteConcrete",
                        ["siteBitFlags"] = i == 0 ? "0x00040021" : "0x00040023",
                        ["formControlParser"] = "msOFormsFormControl",
                        ["formPropMask"] = "0x0C000C48",
                        ["sizeSource"] = "formControlDisplayedSize",
                        ["displayedWidth"] = rawWidth ?? 0,
                        ["displayedHeight"] = rawHeight ?? 0,
                        ["logicalWidth"] = 0,
                        ["logicalHeight"] = 0,
                        ["siteDepth"] = 0,
                        ["siteType"] = 1,
                        ["siteLocalOffset"] = 0,
                        ["generatedStoragePath"] = page.StoragePath,
                        ["generatedStorageF"] = page.FStream,
                        ["generatedStorageO"] = page.OStream,
                        ["generatedStorageCompObjKind"] = "Page",
                        ["generatedPageProperties"] = page.PageProperties,
                        ["multiPageParent"] = name,
                        ["multiPagePageIndex"] = i,
                        ["multiPagePageId"] = page.SiteId,
                        ["multiPageXStreamPath"] = $"{ownedStoragePath}/x"
                    };

                    result.Add(new ControlInfo(
                        page.Name,
                        "Page",
                        0,
                        0,
                        rawWidth,
                        rawHeight,
                        0,
                        0,
                        rawWidth is int pw ? FromRawPoints(pw) : null,
                        rawHeight is int ph ? FromRawPoints(ph) : null,
                        pageProps,
                        name,
                        null,
                        null,
                        null,
                        null,
                        0,
                        null,
                        null,
                        null,
                        null));
                }

                maxId += 1 + pageNames.Length;
            }
            else if (template is null)
            {
                var generated = GeneratedControlFactory.Create(
                    type!,
                    name,
                    maxId,
                    (int)props["tabIndex"]!,
                    left,
                    top,
                    rawWidth,
                    rawHeight,
                    add.Caption,
                    add.Value,
                    props);

                props["generatedFormSitePayload"] = generated.SitePayload;
                props["generatedObjectPayload"] = generated.ObjectPayload;
                props["siteParser"] = "msOFormsOleSiteConcrete";
                props["parser"] = type!.Equals("CommandButton", StringComparison.OrdinalIgnoreCase)
                    ? "msOFormsCommandButton"
                    : type.Equals("Label", StringComparison.OrdinalIgnoreCase)
                        ? "msOFormsLabel"
                        : "msOFormsMorphData";
                props["siteDepth"] = 0;
                props["siteType"] = 1;
                props["siteLocalOffset"] = 0;
                props["cbSite"] = generated.SitePayload.Length - 4;
                props["objectStreamLocalOffset"] = 0;
                props["objectStreamSize"] = generated.ObjectPayload.Length;
                props["siteObjectStreamSize"] = generated.ObjectPayload.Length;
                props["objectStreamSizeFromSite"] = generated.ObjectPayload.Length;

                foreach (var (propertyName, propertyValue) in generated.Metadata)
                {
                    props[propertyName] = propertyValue;
                }
            }

            var source = template ?? new ControlInfo(
                name,
                type!,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                parent,
                null,
                null,
                null,
                null,
                0,
                null,
                null,
                null,
                null);

            result.Add(source with
            {
                Name = name,
                Parent = parent,
                Left = left,
                Top = top,
                RawWidth = rawWidth,
                RawHeight = rawHeight,
                LeftPt = left is int l ? FromRawPoints(l) : source.LeftPt,
                TopPt = top is int t ? FromRawPoints(t) : source.TopPt,
                WidthPt = rawWidth is int w ? FromRawPoints(w) : source.WidthPt,
                HeightPt = rawHeight is int h ? FromRawPoints(h) : source.HeightPt,
                Properties = props
            });
        }

        return result;
    }

    private static int NextTabIndexForParent(IEnumerable<ControlInfo> controls, string? parent)
    {
        var max = controls
            .Where(c => string.Equals(c.Parent, parent, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Properties is not null && TryGetInt(c.Properties, "tabIndex", out var tabIndex) ? tabIndex : -1)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Min(max + 1, ushort.MaxValue);
    }

    private static int NextMultiPagePageIndex(IEnumerable<ControlInfo> controls, string multiPageName)
    {
        var max = controls
            .Where(c => c.Type.Equals("Page", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Parent, multiPageName, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Properties is not null && TryGetInt(c.Properties, "multiPagePageIndex", out var index) ? index : -1)
            .DefaultIfEmpty(-1)
            .Max();
        return Math.Min(max + 1, ushort.MaxValue);
    }

    private static ControlInfo ApplyToControl(ControlInfo control, Dictionary<string, JsonElement>? requested, LayoutPatch? layout, string? newName, string? patchDir = null)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot patch '{control.Name}': control has no object metadata.");
        }

        var props = new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, value) in requested ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
        {
            ApplyPropertyToDictionary(control.Name, props, propertyName, value, patchDir);
        }

        var left = control.Left;
        var top = control.Top;
        var rawWidth = control.RawWidth;
        var rawHeight = control.RawHeight;

        if (layout is not null)
        {
            left = layout.Left ?? ToRawPoints(layout.LeftPt) ?? left;
            top = layout.Top ?? ToRawPoints(layout.TopPt) ?? top;
            rawWidth = layout.RawWidth ?? layout.Width ?? ToRawPoints(layout.WidthPt) ?? rawWidth;
            rawHeight = layout.RawHeight ?? layout.Height ?? ToRawPoints(layout.HeightPt) ?? rawHeight;
        }

        var effectiveName = string.IsNullOrWhiteSpace(newName) ? control.Name : newName.Trim();
        if (!effectiveName.Equals(control.Name, StringComparison.Ordinal))
        {
            if (!props.ContainsKey("nameSpan"))
            {
                throw new CliException($"Cannot rename '{control.Name}': this control does not expose a documented nameSpan in FormSiteData.");
            }

            props["name"] = effectiveName;
            props["nameRaw"] = effectiveName;
            props["siteName"] = effectiveName;
        }

        return control with
        {
            Name = effectiveName,
            Left = left,
            Top = top,
            RawWidth = rawWidth,
            RawHeight = rawHeight,
            LeftPt = left is int l ? FromRawPoints(l) : control.LeftPt,
            TopPt = top is int t ? FromRawPoints(t) : control.TopPt,
            WidthPt = rawWidth is int w ? FromRawPoints(w) : control.WidthPt,
            HeightPt = rawHeight is int h ? FromRawPoints(h) : control.HeightPt,
            Properties = props
        };
    }

    private static void ApplyPropertyToDictionary(string controlName, Dictionary<string, object?> props, string propertyName, JsonElement value, string? patchDir)
    {
        switch (propertyName.ToLowerInvariant())
        {
            case "caption":
            case "value":
            case "groupname":
            case "fontname":
                props[CanonicalPropertyName(propertyName)] = RequireString(controlName, propertyName, value);
                break;
            case "passwordchar":
                var passwordChar = RequireString(controlName, propertyName, value);
                props["passwordChar"] = passwordChar.Length == 0 ? string.Empty : passwordChar[0].ToString();
                break;
            case "tabcaptions":
            case "tabnames":
            case "pagenames":
            case "pagecaptions":
                props[CanonicalPropertyName(propertyName)] = RequireStringArray(controlName, propertyName, value);
                break;
            case "picture":
            case "mouseicon":
                props[CanonicalPropertyName(propertyName)] = RequirePicture(controlName, propertyName, value, patchDir);
                break;
            case "controltiptext":
                if (!props.ContainsKey("controlTipTextSpan"))
                {
                    throw new CliException($"Cannot patch '{controlName}.controlTipText': this control does not expose a documented controlTipTextSpan in FormSiteData.");
                }
                props["controlTipText"] = RequireString(controlName, propertyName, value);
                break;
            case "controlsource":
                if (!props.ContainsKey("controlSourceSpan"))
                {
                    throw new CliException($"Cannot patch '{controlName}.controlSource': this control does not expose a documented controlSourceSpan in FormSiteData. Emit it during add/create first.");
                }
                props["controlSource"] = RequireString(controlName, propertyName, value);
                break;
            case "backcolor":
            case "forecolor":
            case "bordercolor":
                props[CanonicalPropertyName(propertyName)] = RequireColorLikeString(controlName, propertyName, value);
                break;
            case "fontsize":
                var size = RequireFontSize(controlName, value);
                props["fontSize"] = size;
                props["fontHeightRaw"] = (int)Math.Round(size * 20.0, MidpointRounding.AwayFromZero);
                break;
            case "enabled":
            case "locked":
            case "wordwrap":
            case "autosize":
            case "enterkeybehavior":
            case "tabkeybehavior":
            case "selectionmargin":
            case "autowordselect":
            case "hideselection":
            case "autotab":
            case "multiline":
            case "integralheight":
            case "columnheads":
            case "matchrequired":
            case "editable":
                SetVariousPropertyBit(props, propertyName, RequireBoolean(controlName, propertyName, value));
                break;
            case "backstyle":
                SetVariousPropertyBit(props, propertyName, RequireUInt16(controlName, propertyName, value) != 0);
                props["backStyle"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "alignment":
                var alignment = RequireUInt16(controlName, propertyName, value);
                if (alignment is > 1)
                {
                    throw new CliException($"Property '{propertyName}' for '{controlName}' must be 0 or 1.");
                }
                SetVariousPropertyBit(props, propertyName, alignment == 0);
                props["alignment"] = alignment;
                break;
            case "imemode":
                SetImeMode(props, RequireUInt16(controlName, propertyName, value));
                break;
            case "pictureposition":
                props["picturePosition"] = RequireInt32(controlName, propertyName, value);
                break;
            case "mousepointer":
                props["mousePointer"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "maxlength":
                props["maxLength"] = RequireNonNegativeInt32(controlName, propertyName, value);
                break;
            case "scrollbars":
                props["scrollBars"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "displaystyle":
            case "listwidth":
            case "boundcolumn":
            case "listrows":
            case "matchentry":
            case "liststyle":
            case "showdropbuttonwhen":
            case "dropbuttonstyle":
            case "multiselect":
                props[CanonicalPropertyName(propertyName)] = RequireUInt16(controlName, propertyName, value);
                break;
            case "textcolumn":
            case "columncount":
                props[CanonicalPropertyName(propertyName)] = RequireInt16(controlName, propertyName, value);
                break;
            case "dragbehavior":
            case "enterfieldbehavior":
                var behaviorValue = RequireUInt16(controlName, propertyName, value);
                SetVariousPropertyBit(props, propertyName, behaviorValue != 0);
                props[CanonicalPropertyName(propertyName)] = behaviorValue;
                break;
            case "borderstyle":
                props["borderStyle"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "specialeffect":
                props["specialEffect"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "textalign":
                var textAlign = RequireTextAlign(controlName, propertyName, value);
                props["textAlign"] = TextPropsFactory.TextAlignName(textAlign);
                props["paragraphAlign"] = TextPropsFactory.TextAlignToParagraphAlign(textAlign);
                break;
            case "paragraphalign":
                props["paragraphAlign"] = RequireUInt16(controlName, propertyName, value);
                props["textAlign"] = TextPropsFactory.ParagraphAlignToTextAlign((int)props["paragraphAlign"]!);
                break;
            case "accelerator":
                var accelerator = RequireString(controlName, propertyName, value);
                props["accelerator"] = accelerator.Length == 0 ? string.Empty : accelerator[0].ToString();
                if (accelerator.Length > 0)
                {
                    props["acceleratorCode"] = (int)accelerator[0];
                }
                break;
            case "takefocusonclick":
                props["takeFocusOnClick"] = RequireBoolean(controlName, propertyName, value);
                break;
            case "tabindex":
                props["tabIndex"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "tabstop":
            case "visible":
            case "default":
            case "cancel":
                SetSiteFlag(props, propertyName, RequireBoolean(controlName, propertyName, value));
                break;
            case "tag":
                props["tag"] = RequireString(controlName, propertyName, value);
                break;
            case "rowsource":
                props["rowSource"] = RequireString(controlName, propertyName, value);
                break;
            case "helpcontextid":
                props["helpContextId"] = RequireInt32(controlName, propertyName, value);
                break;
            case "groupid":
                props["groupId"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "fontbold":
                props["fontBold"] = RequireBoolean(controlName, propertyName, value);
                break;
            case "fontitalic":
                props["fontItalic"] = RequireBoolean(controlName, propertyName, value);
                break;
            case "fontunderline":
                props["fontUnderline"] = RequireBoolean(controlName, propertyName, value);
                break;
            case "fontstrikethrough":
                props["fontStrikethrough"] = RequireBoolean(controlName, propertyName, value);
                break;
            case "fontcharset":
                props["fontCharSet"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "fontpitchandfamily":
                props["fontPitchAndFamily"] = RequireUInt16(controlName, propertyName, value);
                break;
            case "fontweight":
                props["fontWeight"] = RequireUInt16(controlName, propertyName, value);
                break;
            default:
                break;
        }
    }

    private static void ApplyAddPropertyToDictionary(string controlName, Dictionary<string, object?> props, string propertyName, JsonElement value, string? patchDir)
    {
        switch (propertyName.ToLowerInvariant())
        {
            case "orientation":
                props["orientation"] = RequireInt32(controlName, propertyName, value);
                break;
            case "enabled":
            case "locked":
            case "wordwrap":
            case "autosize":
            case "enterkeybehavior":
            case "tabkeybehavior":
            case "selectionmargin":
            case "autowordselect":
            case "hideselection":
            case "autotab":
            case "multiline":
            case "integralheight":
            case "columnheads":
            case "matchrequired":
            case "editable":
            case "takefocusonclick":
            case "tabstop":
            case "visible":
            case "default":
            case "cancel":
                props[CanonicalPropertyName(propertyName)] = RequireBoolean(controlName, propertyName, value);
                break;
            case "backstyle":
            case "imemode":
            case "pictureposition":
            case "mousepointer":
            case "borderstyle":
            case "specialeffect":
            case "maxlength":
            case "scrollbars":
            case "displaystyle":
            case "listwidth":
            case "boundcolumn":
            case "textcolumn":
            case "columncount":
            case "listrows":
            case "matchentry":
            case "liststyle":
            case "showdropbuttonwhen":
            case "dropbuttonstyle":
            case "multiselect":
            case "dragbehavior":
            case "enterfieldbehavior":
                props[CanonicalPropertyName(propertyName)] = RequireInt32(controlName, propertyName, value);
                break;
            case "alignment":
                var addAlignment = RequireInt32(controlName, propertyName, value);
                if (addAlignment is < 0 or > 1)
                {
                    throw new CliException($"Property '{propertyName}' for '{controlName}' must be 0 or 1.");
                }
                props["alignment"] = addAlignment;
                break;
            case "textalign":
                var addTextAlign = RequireTextAlign(controlName, propertyName, value);
                props["textAlign"] = TextPropsFactory.TextAlignName(addTextAlign);
                props["paragraphAlign"] = TextPropsFactory.TextAlignToParagraphAlign(addTextAlign);
                break;
            case "paragraphalign":
                props["paragraphAlign"] = RequireInt32(controlName, propertyName, value);
                break;
            case "accelerator":
                props["accelerator"] = RequireString(controlName, propertyName, value);
                break;
            case "controlsource":
                props["controlSource"] = RequireString(controlName, propertyName, value);
                break;
            case "tabcaptions":
            case "tabnames":
            case "pagenames":
            case "pagecaptions":
                props[CanonicalPropertyName(propertyName)] = RequireStringArray(controlName, propertyName, value);
                break;
            default:
                ApplyPropertyToDictionary(controlName, props, propertyName, value, patchDir);
                break;
        }
    }

    private const double HimetricPerPoint = 2540.0 / 72.0;

    private static int? ToRawPoints(double? points) =>
        points is null ? null : (int)Math.Round(points.Value * HimetricPerPoint, MidpointRounding.AwayFromZero);

    private static double FromRawPoints(int raw) =>
        Math.Round(raw / HimetricPerPoint, 2, MidpointRounding.AwayFromZero);

    private static string CanonicalPropertyName(string propertyName) =>
        propertyName.ToLowerInvariant() switch
        {
            "groupname" => "groupName",
            "fontname" => "fontName",
            "wordwrap" => "wordWrap",
            "autosize" => "autoSize",
            "backstyle" => "backStyle",
            "alignment" => "alignment",
            "imemode" => "imeMode",
            "pictureposition" => "picturePosition",
            "picturesizemode" => "pictureSizeMode",
            "picturealignment" => "pictureAlignment",
            "picturetiling" => "pictureTiling",
            "mousepointer" => "mousePointer",
            "borderstyle" => "borderStyle",
            "specialeffect" => "specialEffect",
            "maxlength" => "maxLength",
            "passwordchar" => "passwordChar",
            "scrollbars" => "scrollBars",
            "displaystyle" => "displayStyle",
            "listwidth" => "listWidth",
            "boundcolumn" => "boundColumn",
            "textcolumn" => "textColumn",
            "columncount" => "columnCount",
            "listrows" => "listRows",
            "matchentry" => "matchEntry",
            "liststyle" => "listStyle",
            "showdropbuttonwhen" => "showDropButtonWhen",
            "dropbuttonstyle" => "dropButtonStyle",
            "multiselect" => "multiSelect",
            "dragbehavior" => "dragBehavior",
            "enterfieldbehavior" => "enterFieldBehavior",
            "enterkeybehavior" => "enterKeyBehavior",
            "tabkeybehavior" => "tabKeyBehavior",
            "selectionmargin" => "selectionMargin",
            "autowordselect" => "autoWordSelect",
            "hideselection" => "hideSelection",
            "autotab" => "autoTab",
            "multiline" => "multiLine",
            "integralheight" => "integralHeight",
            "columnheads" => "columnHeads",
            "matchrequired" => "matchRequired",
            "takefocusonclick" => "takeFocusOnClick",
            "textalign" => "textAlign",
            "paragraphalign" => "paragraphAlign",
            "tabstop" => "tabStop",
            "controltiptext" => "controlTipText",
            "controlsource" => "controlSource",
            "backcolor" => "backColor",
            "forecolor" => "foreColor",
            "bordercolor" => "borderColor",
            "tabcaptions" => "tabCaptions",
            "tabnames" => "tabNames",
            "pagenames" => "pageNames",
            "pagecaptions" => "pageCaptions",
            "tag" => "tag",
            "rowsource" => "rowSource",
            "helpcontextid" => "helpContextId",
            "groupid" => "groupId",
            "fontbold" => "fontBold",
            "fontitalic" => "fontItalic",
            "fontunderline" => "fontUnderline",
            "fontstrikethrough" => "fontStrikethrough",
            "fontcharset" => "fontCharSet",
            "fontpitchandfamily" => "fontPitchAndFamily",
            "fontweight" => "fontWeight",
            _ => propertyName
        };

    private static void SetVariousPropertyBit(Dictionary<string, object?> props, string propertyName, bool value)
    {
        var bits = TryGetInt(props, "variousPropertyBitsRaw", out var current)
            ? unchecked((uint)current)
            : DefaultVariousPropertyBits(props);

        var bit = propertyName.ToLowerInvariant() switch
        {
            "enabled" => 1,
            "locked" => 2,
            "backstyle" => 3,
            "alignment" => 13,
            "integralheight" => 11,
            "columnheads" => 10,
            "matchrequired" => 12,
            "editable" => 14,
            "dragbehavior" => 19,
            "enterkeybehavior" => 20,
            "enterfieldbehavior" => 21,
            "tabkeybehavior" => 22,
            "wordwrap" => 23,
            "selectionmargin" => 26,
            "autowordselect" => 27,
            "autosize" => 28,
            "hideselection" => 29,
            "autotab" => 30,
            "multiline" => 31,
            _ => throw new CliException($"Property '{propertyName}' is not a supported VariousPropertyBits field.")
        };

        var mask = 1u << bit;
        bits = value ? bits | mask : bits & ~mask;
        props["variousPropertyBitsRaw"] = unchecked((int)bits);
        props[CanonicalPropertyName(propertyName)] = value;
    }

    private static void SetImeMode(Dictionary<string, object?> props, int imeMode)
    {
        if (imeMode is < 0 or > 15)
        {
            throw new CliException("Property 'imeMode' must be between 0 and 15.");
        }

        var bits = TryGetInt(props, "variousPropertyBitsRaw", out var current)
            ? unchecked((uint)current)
            : DefaultVariousPropertyBits(props);
        bits &= ~(0xFu << 15);
        bits |= ((uint)imeMode & 0xFu) << 15;
        props["variousPropertyBitsRaw"] = unchecked((int)bits);
        props["imeMode"] = imeMode;
    }

    private static uint DefaultVariousPropertyBits(Dictionary<string, object?> props) =>
        TryGetString(props, "parser", out var parser) && parser.Equals("msOFormsLabel", StringComparison.OrdinalIgnoreCase)
            ? 0x0080_0013u
            : IsTextBox(props)
                ? 0x2C80_481Bu
            : IsControlType(props, "CheckBox")
                ? 0x2C80_081Bu
            : IsControlType(props, "OptionButton")
                ? 0x0080_001Bu
            : 0x0000_001Bu;

    private static bool IsTextBox(Dictionary<string, object?> props) =>
        IsControlType(props, "TextBox");

    private static bool IsControlType(Dictionary<string, object?> props, string type) =>
        TryGetString(props, "controlType", out var controlType) &&
        controlType.Equals(type, StringComparison.OrdinalIgnoreCase);

    private static void SetSiteFlag(Dictionary<string, object?> props, string propertyName, bool value)
    {
        var flags = TryGetInt(props, "siteBitFlagsRaw", out var current)
            ? unchecked((uint)current)
            : 0x0000_0013;

        var bit = propertyName.ToLowerInvariant() switch
        {
            "tabstop" => 0,
            "visible" => 1,
            "default" => 2,
            "cancel" => 3,
            _ => throw new CliException($"Property '{propertyName}' is not a supported SITE_FLAG field.")
        };

        var mask = 1u << bit;
        flags = value ? flags | mask : flags & ~mask;
        props["siteBitFlagsRaw"] = unchecked((int)flags);
        props[CanonicalPropertyName(propertyName)] = value;
    }

    private static string RequireString(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
    }

    private static string RequirePicture(string controlName, string propertyName, JsonElement value, string? patchDir)
    {
        var s = RequireString(controlName, propertyName, value);
        if (string.IsNullOrWhiteSpace(s)) return s;

        byte[] imgBytes;
        if (s.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            imgBytes = Convert.FromBase64String(s["base64:".Length..]);
            // If the base64 already has the 24-byte header (legacy export), keep it as is.
            if (imgBytes.Length >= 24 && imgBytes[16] == 0x6C && imgBytes[17] == 0x74 && imgBytes[18] == 0x00 && imgBytes[19] == 0x00)
            {
                return s; // Already contains header.
            }
        }
        else if (s.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            var path = s["file://".Length..];
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(patchDir))
            {
                path = Path.Combine(patchDir, path);
            }

            if (!File.Exists(path))
            {
                throw new CliException($"Picture file not found: {path} for control '{controlName}'.");
            }
            imgBytes = File.ReadAllBytes(path);
        }
        else
        {
            throw new CliException($"Picture property for '{controlName}' must be 'base64:...' or 'file://...'.");
        }

        // Attach the 24-byte header
        // GUID: {0BE35204-8F91-11CE-9DE3-00AA004BB851}
        var guidBytes = new byte[] { 0x04, 0x52, 0xE3, 0x0B, 0x91, 0x8F, 0xCE, 0x11, 0x9D, 0xE3, 0x00, 0xAA, 0x00, 0x4B, 0xB8, 0x51 };
        var length = imgBytes.Length;
        var header = new byte[24];
        Array.Copy(guidBytes, 0, header, 0, 16);
        header[16] = 0x6C; // 74 6C is 0x0000746C little-endian -> 6C 74 00 00
        header[17] = 0x74;
        header[18] = 0x00;
        header[19] = 0x00;
        header[20] = (byte)(length & 0xFF);
        header[21] = (byte)((length >> 8) & 0xFF);
        header[22] = (byte)((length >> 16) & 0xFF);
        header[23] = (byte)((length >> 24) & 0xFF);

        var finalBytes = new byte[24 + length];
        Array.Copy(header, 0, finalBytes, 0, 24);
        Array.Copy(imgBytes, 0, finalBytes, 24, length);

        return $"base64:{Convert.ToBase64String(finalBytes)}";
    }

    private static string[] RequireStringArray(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be an array of strings.");
        }

        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new CliException($"Property '{propertyName}' for '{controlName}' must be an array of strings.");
            }

            result.Add(item.GetString() ?? string.Empty);
        }

        if (result.Count == 0)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must contain at least one string.");
        }

        return result.ToArray();
    }

    private static string RequireColorLikeString(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new CliException($"Property '{propertyName}' for '{controlName}' cannot be empty.");
            }

            return text;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var raw))
        {
            return $"&H{raw:X8}&";
        }

        throw new CliException($"Property '{propertyName}' for '{controlName}' must be a VBA color string like '&H00CCCCCC&' or an unsigned integer.");
    }

    private static int RequireUInt16(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed is < 0 or > ushort.MaxValue)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be an integer between 0 and 65535.");
        }

        return parsed;
    }

    private static int RequireInt32(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a 32-bit integer.");
        }

        return parsed;
    }

    private static int RequireNonNegativeInt32(string controlName, string propertyName, JsonElement value)
    {
        var parsed = RequireInt32(controlName, propertyName, value);
        if (parsed < 0)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a non-negative 32-bit integer.");
        }

        return parsed;
    }

    private static int RequireInt16(string controlName, string propertyName, JsonElement value)
    {
        var parsed = RequireInt32(controlName, propertyName, value);
        if (parsed is < short.MinValue or > short.MaxValue)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be an integer between -32768 and 32767.");
        }

        return parsed;
    }

    private static bool RequireBoolean(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i))
        {
            return i != 0;
        }

        throw new CliException($"Property '{propertyName}' for '{controlName}' must be true, false, 1 or 0.");
    }

    private static int RequireTextAlign(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String &&
            TextPropsFactory.TryParseTextAlign(value.GetString() ?? string.Empty, out var named) &&
            named is >= 1 and <= 3)
        {
            return named;
        }

        if (value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out var numeric) &&
            numeric is >= 1 and <= 3)
        {
            return numeric;
        }

        throw new CliException($"Property '{propertyName}' for '{controlName}' must be 'left', 'center', 'right', or an integer from 1 to 3.");
    }

    private static double RequireFontSize(string controlName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var size))
        {
            throw new CliException($"Property 'fontSize' for '{controlName}' must be numeric.");
        }

        if (size is <= 0 or > 72)
        {
            throw new CliException($"Property 'fontSize' for '{controlName}' must be between 0 and 72.");
        }

        return size;
    }

    private static bool TryGetString(Dictionary<string, object?> props, string key, out string value)
    {
        value = string.Empty;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is string text)
        {
            value = text;
            return true;
        }

        value = raw.ToString() ?? string.Empty;
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryGetInt(Dictionary<string, object?> props, string key, out int value)
    {
        value = 0;
        if (!props.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case int i:
                value = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                value = (int)l;
                return true;
            case uint u when u <= int.MaxValue:
                value = (int)u;
                return true;
            case ulong ul when ul <= int.MaxValue:
                value = (int)ul;
                return true;
            case short s:
                value = s;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte sb:
                value = sb;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var parsed):
                value = parsed;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var parsed64) && parsed64 >= int.MinValue && parsed64 <= int.MaxValue:
                value = (int)parsed64;
                return true;
            case JsonElement element when element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var parsedU64) && parsedU64 <= int.MaxValue:
                value = (int)parsedU64;
                return true;
            case string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static void SetFormBooleanPropertyBit(Dictionary<string, object?> props, string propertyName, bool value)
    {
        var bits = TryGetString(props, "formBooleanProperties", out var hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt32(hex[2..], 16)
            : 0x00200001u;

        var bit = propertyName.ToLowerInvariant() switch
        {
            "enabled" => 0,
            "picturetiling" => 4,
            "keepscrollbarsvisible" => 21,
            "righttoleft" => 22,
            _ => throw new CliException($"Property '{propertyName}' is not a supported formBooleanProperties field.")
        };

        var mask = 1u << bit;
        bits = value ? bits | mask : bits & ~mask;
        props["formBooleanProperties"] = $"0x{bits:X8}";
    }

    private static void ApplyFormPropertyToDictionary(string formKey, Dictionary<string, object?> props, string propertyName, JsonElement value, string? patchDir)
    {
        var normalizedPropertyName = propertyName.ToLowerInvariant() switch
        {
            "backcolor" => "formBackColor",
            "forecolor" => "formForeColor",
            "bordercolor" => "formBorderColor",
            "caption" => "formCaption",
            "borderstyle" => "formBorderStyle",
            "mousepointer" => "formMousePointer",
            "scrollbars" => "formScrollBars",
            "cycle" => "formCycle",
            "specialeffect" => "formSpecialEffect",
            "picturealignment" => "formPictureAlignment",
            "picturesizemode" => "formPictureSizeMode",
            "zoom" => "formZoom",
            "widthpt" => "displayedWidthPt",
            "heightpt" => "displayedHeightPt",
            "width" => "Width",
            "height" => "Height",
            "clientwidth" => "ClientWidth",
            "clientheight" => "ClientHeight",
            "left" => "Left",
            "top" => "Top",
            "clientleft" => "ClientLeft",
            "clienttop" => "ClientTop",
            "startupposition" => "StartUpPosition",
            "showmodal" => "ShowModal",
            "tag" => "Tag",
            "drawbuffer" => "DrawBuffer",
            _ => propertyName
        };

        switch (normalizedPropertyName.ToLowerInvariant())
        {
            case "formbackcolor":
            case "formforecolor":
            case "formbordercolor":
                var colorStr = RequireColorLikeString(formKey, normalizedPropertyName, value);
                props[CanonicalPropertyName(normalizedPropertyName)] = colorStr;
                props[CanonicalPropertyName(normalizedPropertyName) + "Raw"] = MsFormsFactoryBinary.ParseColor(colorStr, 0);
                break;
            case "formcaption":
            case "tag":
                props[CanonicalPropertyName(normalizedPropertyName)] = RequireString(formKey, normalizedPropertyName, value);
                break;
            case "enabled":
            case "picturetiling":
            case "keepscrollbarsvisible":
            case "righttoleft":
            case "showmodal":
                var boolVal = RequireBoolean(formKey, normalizedPropertyName, value);
                if (normalizedPropertyName.Equals("showmodal", StringComparison.OrdinalIgnoreCase))
                {
                    props["ShowModal"] = boolVal;
                }
                else
                {
                    SetFormBooleanPropertyBit(props, normalizedPropertyName, boolVal);
                }
                break;
            case "formborderstyle":
            case "formmousepointer":
            case "formscrollbars":
            case "formcycle":
            case "formspecialeffect":
            case "formpicturealignment":
            case "formpicturesizemode":
                props[CanonicalPropertyName(normalizedPropertyName)] = RequireUInt16(formKey, normalizedPropertyName, value);
                break;
            case "formzoom":
            case "nextavailableid":
                props[CanonicalPropertyName(normalizedPropertyName)] = RequireUInt32(formKey, normalizedPropertyName, value);
                break;
            case "displayedwidth":
            case "displayedheight":
            case "logicalwidth":
            case "logicalheight":
            case "scrollleft":
            case "scrolltop":
                props[CanonicalPropertyName(normalizedPropertyName)] = RequireInt32(formKey, normalizedPropertyName, value);
                break;
            case "displayedwidthpt":
            case "displayedheightpt":
            case "logicalwidthpt":
            case "logicalheightpt":
            case "left":
            case "top":
            case "clientleft":
            case "clienttop":
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var doubleVal))
                {
                    props[CanonicalPropertyName(normalizedPropertyName)] = doubleVal;
                }
                else
                {
                    throw new CliException($"Property '{normalizedPropertyName}' for '{formKey}' must be a number.");
                }
                break;
            case "startupposition":
            case "drawbuffer":
                props[CanonicalPropertyName(normalizedPropertyName)] = RequireInt32(formKey, normalizedPropertyName, value);
                break;
            default:
                break;
        }
    }

    private static uint RequireUInt32(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var parsed))
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a 32-bit unsigned integer.");
        }

        return parsed;
    }
}
