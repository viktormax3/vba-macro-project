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
        "borderColor"
    };

    private static readonly HashSet<string> FormSitePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tabIndex",
        "controlTipText"
    };

    public static LayoutInspection ApplyObjectPropertyPatch(LayoutInspection source, PatchDocument patch, bool allowFormSitePatch = false)
    {
        ValidateObjectPatch(patch, allowFormSitePatch);

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

            controls.Add(ApplyToControl(control, requested, layout, newName));
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
            controls.AddRange(BuildMovedControls(source.Controls, controls, moveByName, patchedByName, layoutByName));
        }

        if (allowFormSitePatch && patch.Add is { Count: > 0 })
        {
            controls.AddRange(BuildAddedControls(source.Controls, controls, patch.Add));
        }

        return source with { Controls = controls, RemovedControls = removedControls, RemovedStoragePaths = removalPlan.StoragePaths };
    }

    public static void ValidateObjectPatch(PatchDocument patch, bool allowFormSitePatch = false)
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
            foreach (var propertyName in properties.Keys)
            {
                if (!ObjectPropertyNames.Contains(propertyName) && !(allowFormSitePatch && FormSitePropertyNames.Contains(propertyName)))
                {
                    var supported = ObjectPropertyNames.Concat(allowFormSitePatch ? FormSitePropertyNames : Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                    throw new CliException($"Rebuild patch cannot write '{controlName}.{propertyName}' yet. Supported properties: {string.Join(", ", supported)}.");
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
                throw new CliException($"Add entry '{add.Name}' requested type '{add.Type}', but this build can create only: {GeneratedControlFactory.SupportedTypes}, Frame. Use 'fromTemplate' for other types.");
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

    private static string FormatStorageId(int id) => id is >= 0 and < 10 ? $"0{id}" : id.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<ControlInfo> BuildMovedControls(
        IReadOnlyList<ControlInfo> templateControls,
        IReadOnlyList<ControlInfo> existingControls,
        Dictionary<string, string?> moveByName,
        Dictionary<string, Dictionary<string, JsonElement>> patchedByName,
        Dictionary<string, LayoutPatch> layoutByName)
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

        return BuildAddedControls(templateControls, existingControls, additions);
    }

    private static IEnumerable<ControlInfo> BuildAddedControls(IReadOnlyList<ControlInfo> templateControls, IReadOnlyList<ControlInfo> existingControls, IReadOnlyList<AddControlPatch> additions)
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

            var targetStoragePath = ResolveTargetStoragePath(parent, existingControls.Concat(result).ToList());
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
                ApplyPropertyToDictionary(name, props, propertyName, value);
            }

            var left = add.Left ?? ToRawPoints(add.LeftPt) ?? template?.Left ?? 0;
            var top = add.Top ?? ToRawPoints(add.TopPt) ?? template?.Top ?? 0;
            var rawWidth = add.RawWidth ?? add.Width ?? ToRawPoints(add.WidthPt) ?? template?.RawWidth;
            var rawHeight = add.RawHeight ?? add.Height ?? ToRawPoints(add.HeightPt) ?? template?.RawHeight;

            if (template is null && type.Equals("Frame", StringComparison.OrdinalIgnoreCase))
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

    private static ControlInfo ApplyToControl(ControlInfo control, Dictionary<string, JsonElement>? requested, LayoutPatch? layout, string? newName)
    {
        if (control.Properties is null)
        {
            throw new CliException($"Cannot patch '{control.Name}': control has no object metadata.");
        }

        var props = new Dictionary<string, object?>(control.Properties, StringComparer.OrdinalIgnoreCase);
        foreach (var (propertyName, value) in requested ?? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase))
        {
            ApplyPropertyToDictionary(control.Name, props, propertyName, value);
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

    private static void ApplyPropertyToDictionary(string controlName, Dictionary<string, object?> props, string propertyName, JsonElement value)
    {
        switch (propertyName.ToLowerInvariant())
        {
            case "caption":
            case "value":
            case "groupname":
            case "fontname":
                props[CanonicalPropertyName(propertyName)] = RequireString(controlName, propertyName, value);
                break;
            case "tabcaptions":
            case "tabnames":
                props[CanonicalPropertyName(propertyName)] = RequireStringArray(controlName, propertyName, value);
                break;
            case "controltiptext":
                if (!props.ContainsKey("controlTipTextSpan"))
                {
                    throw new CliException($"Cannot patch '{controlName}.controlTipText': this control does not expose a documented controlTipTextSpan in FormSiteData.");
                }
                props["controlTipText"] = RequireString(controlName, propertyName, value);
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
            case "tabindex":
                props["tabIndex"] = RequireUInt16(controlName, propertyName, value);
                break;
            default:
                throw new CliException($"Property '{propertyName}' is not supported by rebuild patch.");
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
            "controltiptext" => "controlTipText",
            "backcolor" => "backColor",
            "forecolor" => "foreColor",
            "bordercolor" => "borderColor",
            "tabcaptions" => "tabCaptions",
            "tabnames" => "tabNames",
            _ => propertyName
        };

    private static string RequireString(string controlName, string propertyName, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliException($"Property '{propertyName}' for '{controlName}' must be a string.");
        }

        return value.GetString() ?? string.Empty;
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
}
