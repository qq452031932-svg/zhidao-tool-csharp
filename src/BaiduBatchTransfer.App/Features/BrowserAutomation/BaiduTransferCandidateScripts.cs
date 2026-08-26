using System.Collections.Immutable;
using System.Text.Json;
using BaiduBatchTransfer.BaiduAdapter.WebAutomation;
using BaiduBatchTransfer.Core.Domain;

namespace BaiduBatchTransfer.App.Features.BrowserAutomation;

public static class BaiduTransferCandidateScripts
{
    private const int MaximumResponseLength = 65_536;

    private static readonly HashSet<string> ResponseFields = new(StringComparer.Ordinal)
    {
        "status", "reason", "officialOriginMatched", "pageState", "controlCount",
        "baiduUserId", "parentExactId", "canonicalPath", "ancestorExactIds", "totalCount",
        "reachedEnd", "complete", "stable", "generationToken", "items",
        "observationToken", "mutationCount",
    };

    private static readonly HashSet<string> ItemFields = new(StringComparer.Ordinal)
    {
        "exactId", "kind", "serverName",
    };

    public const string ObserveScript = """
        async function(argsJson) {
          'use strict';
          const args = JSON.parse(argsJson);
          const fail = (reason) => ({
            status: 'notValidated', reason, officialOriginMatched: false,
            pageState: args.expectedPageState, controlCount: 0, baiduUserId: '',
            parentExactId: null, canonicalPath: null, ancestorExactIds: [], totalCount: 0,
            reachedEnd: false, complete: false, stable: false, generationToken: 'none',
            items: [], observationToken: '', mutationCount: 0
          });
          const url = new URL(location.href);
          const official = url.protocol === 'https:' && url.hostname === 'pan.baidu.com' &&
            url.port === '' && url.username === '' && url.password === '';
          const route = /^\/s\/[^/]+/.test(url.pathname) || /^\/share\//.test(url.pathname) ||
            /^\/disk\//.test(url.pathname) || url.pathname === '/disk/main';
          if (!official || !route || location.href !== args.officialUri) return fail('official-route-mismatch');
          const visible = (node) => {
            if (!node || !node.isConnected || node.disabled === true ||
                node.getAttribute('aria-disabled') === 'true') return false;
            const style = getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' ||
                Number(style.opacity) === 0 || style.pointerEvents === 'none') return false;
            const rect = node.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            const point = document.elementFromPoint(
              Math.max(0, Math.min(innerWidth - 1, rect.left + rect.width / 2)),
              Math.max(0, Math.min(innerHeight - 1, rect.top + rect.height / 2)));
            return point === node || node.contains(point);
          };
          const role = (node) => (node.getAttribute('role') || node.tagName || '').toLowerCase();
          const label = (node) => `${node.getAttribute('aria-label') || ''} ${node.textContent || ''}`.trim();
          const roots = Array.from(document.querySelectorAll('main,form,[role="main"],[role="dialog"],[role="alertdialog"]'))
            .filter(visible).filter(node => role(node) !== '' && label(node).length > 0);
          if (roots.length === 0) return fail('trusted-root-missing');
          if (Array.from(document.querySelectorAll('*')).some(node =>
              node.shadowRoot && node.getAttribute('data-baidu-approved-shadow-root') !== 'true'))
            return fail('unapproved-shadow-root');
          const semantics = {
            ExtractionCode: /提取文件|提取码/,
            ExternalShare: /保存到网盘|转存|分享文件/,
            DestinationPicker: /保存到|选择保存位置|新建文件夹/,
            OwnDrive: /我的网盘|我的资源|全部文件/,
            TransferResult: /保存成功|转存成功|已保存到网盘/
          };
          const matcher = semantics[args.expectedPageState];
          if (!matcher) return fail('unknown-page-state');
          const trusted = roots.filter(node => matcher.test(label(node)));
          if (trusted.length !== 1) return fail('trusted-root-not-unique');
          const root = trusted[0];
          const controls = Array.from(root.querySelectorAll('button,input,[role="button"],[role="textbox"],[role="dialog"]'))
            .filter(visible).filter(node => matcher.test(label(node)) || args.expectedPageState === 'OwnDrive');
          if (controls.length < 1) return fail('candidate-not-unique');
          const controlCount = trusted.length;
          const uidNodes = Array.from(document.querySelectorAll('[data-uid],[data-uk]'));
          const uids = Array.from(new Set(uidNodes.map(node =>
            node.getAttribute('data-uid') || node.getAttribute('data-uk')).filter(Boolean)));
          if (uids.length !== 1 || uids[0] !== args.expectedBaiduUserId) return fail('uid-mismatch');
          const rows = Array.from(root.querySelectorAll('[data-fsid],[data-file-id],[data-id]')).filter(visible);
          const seen = new Set();
          const items = [];
          for (const row of rows) {
            const exactId = row.getAttribute('data-fsid') || row.getAttribute('data-file-id') || row.getAttribute('data-id');
            if (!exactId || seen.has(exactId)) return fail('duplicate-stable-id');
            seen.add(exactId);
            const serverName = row.getAttribute('data-name') || row.getAttribute('title') ||
              row.getAttribute('aria-label');
            if (!serverName) return fail('server-name-missing');
            const kind = row.getAttribute('data-kind') === 'folder' || row.getAttribute('aria-label') === '文件夹'
              ? 'Folder' : 'File';
            items.push({ exactId, kind, serverName });
          }
          items.sort((left, right) => left.exactId < right.exactId ? -1 : left.exactId > right.exactId ? 1 : 0);
          const totalNode = root.querySelector('[data-total-count],[aria-label^="共"]');
          const totalCount = totalNode ? Number(totalNode.getAttribute('data-total-count')) : items.length;
          const endNode = root.querySelector('[data-list-end="true"],[aria-label="已加载全部"]');
          const reachedEnd = Boolean(endNode) || totalCount === items.length;
          const cursor = root.getAttribute('data-cursor') || 'end';
          const previousCursor = root.getAttribute('data-previous-cursor') || '';
          if (cursor !== 'end' && cursor === previousCursor) return fail('cursor-cycle');
          if (cursor !== 'end' && previousCursor === '') return fail('cursor-gap');
          const generation = root.getAttribute('data-generation') || root.getAttribute('data-list-version');
          if (!generation) return fail('generation-missing');
          if (!Number.isInteger(totalCount) || totalCount !== items.length || !reachedEnd)
            return fail('enumeration-incomplete');
          const parentExactId = root.getAttribute('data-parent-id') || null;
          const canonicalPath = root.getAttribute('data-path') || null;
          const ancestorExactIds = Array.from(root.querySelectorAll('[data-ancestor-id]'))
            .map(node => node.getAttribute('data-ancestor-id')).filter(Boolean);
          if ((args.expectedParentExactId !== null && parentExactId !== args.expectedParentExactId) ||
              (args.expectedCanonicalPath !== null && canonicalPath !== args.expectedCanonicalPath))
            return fail('target-binding-mismatch');
          if ((parentExactId === null) !== (ancestorExactIds.length === 0) ||
              parentExactId !== null && ancestorExactIds[ancestorExactIds.length - 1] !== parentExactId)
            return fail('ancestor-chain-mismatch');
          if (args.expectedExactIds.length > 0 &&
              JSON.stringify(items.map(item => item.exactId)) !== JSON.stringify(args.expectedExactIds))
            return fail('exact-item-mismatch');
          await Promise.resolve();
          const stableGeneration = root.getAttribute('data-generation') || root.getAttribute('data-list-version');
          if (stableGeneration !== generation) return fail('changing-generation');
          const evidence = JSON.stringify({
            pageState: args.expectedPageState, baiduUserId: uids[0], parentExactId,
            canonicalPath, ancestorExactIds, totalCount, reachedEnd: true,
            complete: true, stable: true, generationToken: generation, items
          });
          const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(evidence));
          const observationToken = Array.from(new Uint8Array(digest))
            .map(value => value.toString(16).padStart(2, '0')).join('');
          return {
            status: 'validated', reason: 'none', officialOriginMatched: true,
            pageState: args.expectedPageState, controlCount, baiduUserId: uids[0],
            parentExactId, canonicalPath, ancestorExactIds, totalCount, reachedEnd: true,
            complete: true, stable: true, generationToken: generation, items,
            observationToken, mutationCount: 0
          };
        }
        """;

    public const string MutateScript = """
        async function(argsJson) {
          'use strict';
          const args = JSON.parse(argsJson);
          const fail = (reason) => ({
            status: 'notValidated', reason, officialOriginMatched: false,
            pageState: args.expectedPageState, controlCount: 0, baiduUserId: '',
            parentExactId: null, canonicalPath: null, ancestorExactIds: [], totalCount: 0,
            reachedEnd: false, complete: false, stable: false, generationToken: 'none',
            items: [], observationToken: '', mutationCount: 0
          });
          const url = new URL(location.href);
          if (url.protocol !== 'https:' || url.hostname !== 'pan.baidu.com' || url.port !== '' ||
              location.href !== args.officialUri) return fail('official-route-mismatch');
          const visible = (node) => {
            if (!node || !node.isConnected || node.disabled === true ||
                node.getAttribute('aria-disabled') === 'true') return false;
            const style = getComputedStyle(node);
            if (style.display === 'none' || style.visibility === 'hidden' ||
                Number(style.opacity) === 0 || style.pointerEvents === 'none') return false;
            const rect = node.getBoundingClientRect();
            if (rect.width <= 0 || rect.height <= 0) return false;
            const top = document.elementFromPoint(
              Math.max(0, Math.min(innerWidth - 1, rect.left + rect.width / 2)),
              Math.max(0, Math.min(innerHeight - 1, rect.top + rect.height / 2)));
            return top === node || node.contains(top);
          };
          if (Array.from(document.querySelectorAll('*')).some(node =>
              node.shadowRoot && node.getAttribute('data-baidu-approved-shadow-root') !== 'true'))
            return fail('unapproved-shadow-root');
          const label = (node) => `${node.getAttribute('aria-label') || ''} ${node.textContent || ''}`.trim();
          const semantics = {
            ExtractionCode: /提取文件|提取码/,
            ExternalShare: /保存到网盘|转存|分享文件/,
            DestinationPicker: /保存到|选择保存位置|新建文件夹/,
            OwnDrive: /我的网盘|我的资源|全部文件/,
            TransferResult: /保存成功|转存成功|已保存到网盘/
          };
          const matcher = semantics[args.expectedPageState];
          const roots = Array.from(document.querySelectorAll('main,form,[role="main"],[role="dialog"],[role="alertdialog"]'))
            .filter(visible).filter(node => matcher && matcher.test(label(node)));
          if (roots.length !== 1) return fail('trusted-root-not-unique');
          const root = roots[0];
          if (!(root.getAttribute('role') || root.tagName)) return fail('trusted-root-role-missing');
          const uidValues = Array.from(new Set(Array.from(document.querySelectorAll('[data-uid],[data-uk]'))
            .map(node => node.getAttribute('data-uid') || node.getAttribute('data-uk')).filter(Boolean)));
          if (uidValues.length !== 1 || uidValues[0] !== args.expectedBaiduUserId)
            return fail('uid-mismatch');
          const rows = Array.from(root.querySelectorAll('[data-fsid],[data-file-id],[data-id]')).filter(visible);
          const seen = new Set();
          const items = [];
          for (const row of rows) {
            const exactId = row.getAttribute('data-fsid') || row.getAttribute('data-file-id') || row.getAttribute('data-id');
            const serverName = row.getAttribute('data-name') || row.getAttribute('title') ||
              row.getAttribute('aria-label');
            if (!exactId || !serverName || seen.has(exactId)) return fail('item-evidence-invalid');
            seen.add(exactId);
            items.push({ exactId,
              kind: row.getAttribute('data-kind') === 'folder' ? 'Folder' : 'File', serverName });
          }
          items.sort((left, right) => left.exactId < right.exactId ? -1 : left.exactId > right.exactId ? 1 : 0);
          if (JSON.stringify(items.map(item => item.exactId)) !== JSON.stringify(args.expectedExactIds))
            return fail('exact-item-mismatch');
          const parentExactId = root.getAttribute('data-parent-id') || null;
          const canonicalPath = root.getAttribute('data-path') || null;
          const ancestorExactIds = Array.from(root.querySelectorAll('[data-ancestor-id]'))
            .map(node => node.getAttribute('data-ancestor-id')).filter(Boolean);
          if (parentExactId !== args.expectedParentExactId || canonicalPath !== args.expectedCanonicalPath ||
              JSON.stringify(ancestorExactIds) !== JSON.stringify(args.expectedAncestorExactIds))
            return fail('target-binding-mismatch');
          const totalCount = items.length;
          const generation = root.getAttribute('data-generation') || root.getAttribute('data-list-version');
          if (totalCount !== args.expectedTotalCount || generation !== args.expectedGenerationToken)
            return fail('enumeration-changed');
          const evidence = JSON.stringify({
            pageState: args.expectedPageState, baiduUserId: uidValues[0], parentExactId,
            canonicalPath, ancestorExactIds, totalCount, reachedEnd: true,
            complete: true, stable: true, generationToken: generation, items
          });
          const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(evidence));
          const observationToken = Array.from(new Uint8Array(digest))
            .map(value => value.toString(16).padStart(2, '0')).join('');
          if (observationToken !== args.expectedObservationToken) return fail('observation-changed');
          const unique = (selector, pattern) => {
            const found = Array.from(root.querySelectorAll(selector)).filter(visible)
              .filter(node => pattern.test(label(node)));
            return found.length === 1 ? found[0] : null;
          };
          const setValue = (input, value) => {
            const descriptor = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
            if (!descriptor || typeof descriptor.set !== 'function' || !(input instanceof HTMLInputElement))
              return false;
            descriptor.set.call(input, value);
            input.dispatchEvent(new Event('input', { bubbles: true }));
            input.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
          };
          let target = null;
          switch (args.action) {
            case 'SetExtractionCode':
              target = unique('input', /提取码|访问码/);
              if (!target || typeof args.typedValue !== 'string') return fail('action-target-not-unique');
              if (!setValue(target, args.typedValue)) return fail('action-target-not-unique');
              break;
            case 'SubmitExtraction':
              target = unique('button,[role="button"]', /提取文件|确认提取/);
              if (!target) return fail('action-target-not-unique'); target.click(); break;
            case 'OpenDestinationPicker':
              target = unique('button,[role="button"]', /保存到网盘|转存/);
              if (!target) return fail('action-target-not-unique'); target.click(); break;
            case 'SelectExactItems':
              if (rows.length !== args.expectedExactIds.length) return fail('action-target-not-unique');
              for (const row of rows) {
                const checkbox = row.querySelector('input[type="checkbox"],[role="checkbox"]');
                if (!visible(checkbox)) return fail('action-target-not-unique'); checkbox.click();
              }
              break;
            case 'EnterDirectory':
              {
                const rowMatches = rows.filter(row =>
                  (row.getAttribute('data-fsid') || row.getAttribute('data-file-id') ||
                    row.getAttribute('data-id')) === args.targetExactId);
                const ancestorMatches = Array.from(root.querySelectorAll('[data-ancestor-id]')).filter(visible)
                  .filter(node => node.getAttribute('data-ancestor-id') === args.targetExactId);
                target = rowMatches.length === 1 && ancestorMatches.length === 0 ? rowMatches[0] :
                  ancestorMatches.length === 1 && rowMatches.length === 0 ? ancestorMatches[0] : null;
                if (!target) return fail('action-target-not-unique');
                if (ancestorMatches.length === 1) target.click();
                else target.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
              }
              break;
            case 'CreateDirectory':
              target = unique('button,[role="button"]', /新建文件夹/);
              if (!target || typeof args.typedValue !== 'string') return fail('action-target-not-unique');
              target.click(); await Promise.resolve();
              target = unique('input', /文件夹名称|新建文件夹/);
              if (!target || !setValue(target, args.typedValue))
                return fail('action-target-not-unique'); break;
            case 'ConfirmDirectoryCreation':
              target = unique('button,[role="button"]', /确定|创建/);
              if (!target) return fail('action-target-not-unique'); target.click(); break;
            case 'CancelDestinationPicker':
              target = unique('button,[role="button"]', /取消/);
              if (!target) return fail('action-target-not-unique'); target.click(); break;
            case 'ClickConfirmTransfer':
              target = unique('button,[role="button"]', /确定|确认转存|保存到这里/);
              if (!target) return fail('action-target-not-unique'); target.click(); break;
            default: return fail('action-not-allowlisted');
          }
          return {
            status: 'validated', reason: 'none', officialOriginMatched: true,
            pageState: args.expectedPageState, controlCount: 1, baiduUserId: uidValues[0],
            parentExactId, canonicalPath, ancestorExactIds, totalCount, reachedEnd: true,
            complete: true, stable: true, generationToken: generation, items,
            observationToken, mutationCount: 1
          };
        }
        """;

    public static BaiduTransferCandidateDomResult ParseAndValidate(
        string raw,
        BaiduCandidateDomReadRequest request,
        int expectedMutationCount = 0,
        string? expectedObservationToken = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumResponseLength)
            return Invalid();
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 6,
            });
            var root = document.RootElement;
            if (!HasExactFields(root, ResponseFields)) return Invalid();
            var status = RequiredText(root, "status", 32);
            _ = RequiredText(root, "reason", 128);
            if (!string.Equals(status, "validated", StringComparison.Ordinal)) return Invalid();
            if (!string.Equals(root.GetProperty("reason").GetString(), "none", StringComparison.Ordinal) ||
                !root.GetProperty("officialOriginMatched").GetBoolean())
                return Invalid();
            if (!Enum.TryParse<BaiduCandidatePageState>(RequiredText(root, "pageState", 64), false, out var pageState) ||
                pageState != request.ExpectedPageState)
                return Invalid();
            if (root.GetProperty("controlCount").GetInt32() != 1 ||
                root.GetProperty("mutationCount").GetInt32() != expectedMutationCount)
                return Invalid();
            var uid = RequiredText(root, "baiduUserId", 512);
            if (!string.Equals(uid, request.ExpectedBaiduUserId, StringComparison.Ordinal)) return Invalid();

            var parent = OptionalText(root, "parentExactId", 512);
            var pathText = OptionalText(root, "canonicalPath", 2_048);
            var path = pathText is null ? null : new RemotePath(pathText);
            if (request.ExpectedParentExactId is not null &&
                    !string.Equals(parent, request.ExpectedParentExactId, StringComparison.Ordinal) ||
                request.ExpectedCanonicalPath is not null && !Equals(path, request.ExpectedCanonicalPath))
                return Invalid();
            var ancestors = ReadTextArray(root.GetProperty("ancestorExactIds"), 64, 512, requireSorted: false);
            if (ancestors.Distinct(StringComparer.Ordinal).Count() != ancestors.Length) return Invalid();
            if (parent is null)
            {
                if (!ancestors.IsEmpty || path is not null) return Invalid();
            }
            else
            {
                var pathDepth = path?.Value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length ?? -1;
                if (path is null || pathDepth == 0 && !ancestors.IsEmpty ||
                    pathDepth > 0 && (ancestors.IsEmpty ||
                    !string.Equals(ancestors[^1], parent, StringComparison.Ordinal) ||
                    pathDepth != ancestors.Length))
                    return Invalid();
            }

            var items = ReadItems(root.GetProperty("items"));
            var totalCount = root.GetProperty("totalCount").GetInt32();
            var reachedEnd = root.GetProperty("reachedEnd").GetBoolean();
            var complete = root.GetProperty("complete").GetBoolean();
            var stable = root.GetProperty("stable").GetBoolean();
            if (totalCount != items.Length || !reachedEnd || !complete || !stable) return Invalid();
            if (!request.ExpectedExactIds.IsEmpty && !items.Select(item => item.ExactId)
                    .SequenceEqual(request.ExpectedExactIds, StringComparer.Ordinal))
                return Invalid();
            var generation = RequiredText(root, "generationToken", 512);
            var observation = RequiredText(root, "observationToken", 64);
            if (!IsLowercaseFingerprint(observation) || expectedObservationToken is not null &&
                !string.Equals(observation, expectedObservationToken, StringComparison.Ordinal)) return Invalid();

            return BaiduTransferCandidateDomResult.Success(new(
                pageState, uid, parent, path, ancestors, items, totalCount, reachedEnd,
                complete, stable, generation, observation));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or
                                           FormatException or ArgumentException or OverflowException)
        {
            return Invalid();
        }
    }

    public static bool Matches(
        BaiduCandidateDomReadRequest request,
        BaiduTransferCandidateObservation observation) =>
        request is not null && observation is not null &&
        observation.PageState == request.ExpectedPageState &&
        string.Equals(observation.BaiduUserId, request.ExpectedBaiduUserId, StringComparison.Ordinal) &&
        (request.ExpectedParentExactId is null || string.Equals(
            observation.ParentExactId, request.ExpectedParentExactId, StringComparison.Ordinal)) &&
        (request.ExpectedCanonicalPath is null || Equals(
            observation.CanonicalPath, request.ExpectedCanonicalPath)) &&
        (request.ExpectedExactIds.IsEmpty || observation.Items.Select(item => item.ExactId)
            .SequenceEqual(request.ExpectedExactIds, StringComparer.Ordinal)) &&
        observation.TotalCount == observation.Items.Length && observation.ReachedEnd &&
        observation.IsComplete && observation.IsStable &&
        !string.IsNullOrWhiteSpace(observation.GenerationToken) &&
        IsLowercaseFingerprint(observation.ObservationToken);

    private static ImmutableArray<BaiduTransferCandidateItem> ReadItems(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 500)
            throw new JsonException("The item set is invalid.");
        var items = ImmutableArray.CreateBuilder<BaiduTransferCandidateItem>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (!HasExactFields(item, ItemFields)) throw new JsonException("An item shape is invalid.");
            if (!Enum.TryParse<RemoteResourceKind>(RequiredText(item, "kind", 16), false, out var kind) ||
                !Enum.IsDefined(kind))
                throw new JsonException("An item kind is invalid.");
            items.Add(new(
                RequiredText(item, "exactId", 512), kind, RequiredText(item, "serverName", 512)));
        }
        var result = items.ToImmutable();
        if (!result.Select(item => item.ExactId)
                .SequenceEqual(result.Select(item => item.ExactId).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            result.Select(item => item.ExactId).Distinct(StringComparer.Ordinal).Count() != result.Length)
            throw new JsonException("Exact IDs must be strictly ordered and unique.");
        return result;
    }

    private static ImmutableArray<string> ReadTextArray(
        JsonElement element,
        int maximumCount,
        int maximumLength,
        bool requireSorted)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maximumCount)
            throw new JsonException("The text array is invalid.");
        var result = element.EnumerateArray().Select(value =>
        {
            if (value.ValueKind != JsonValueKind.String) throw new JsonException("Text was expected.");
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text) || text.Length > maximumLength || text.Any(char.IsControl))
                throw new JsonException("The bounded text is invalid.");
            return text;
        }).ToImmutableArray();
        if (requireSorted && !result.SequenceEqual(result.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new JsonException("The values are not ordered.");
        return result!;
    }

    private static bool HasExactFields(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var properties = element.EnumerateObject().ToArray();
        return properties.Length == expected.Count &&
            properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == properties.Length &&
            properties.All(property => expected.Contains(property.Name));
    }

    private static string RequiredText(JsonElement root, string name, int maximumLength)
    {
        if (root.GetProperty(name).ValueKind != JsonValueKind.String)
            throw new JsonException("Text was expected.");
        var value = root.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new JsonException("The bounded text is invalid.");
        return value;
    }

    private static string? OptionalText(JsonElement root, string name, int maximumLength)
    {
        var element = root.GetProperty(name);
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind != JsonValueKind.String) throw new JsonException("Text or null was expected.");
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new JsonException("The bounded text is invalid.");
        return value;
    }

    private static bool IsLowercaseFingerprint(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static BaiduTransferCandidateDomResult Invalid() =>
        BaiduTransferCandidateDomResult.Denied(BaiduAutomationDenialReason.ObservationNotValidated);
}
