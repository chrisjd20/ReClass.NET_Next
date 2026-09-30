using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using ReClassNET.Project;

namespace ReClassNET.Patching
{
    public sealed class PatchRepository
    {
        public const string CustomDataKey = "ReClassNET.Next.Patches";
        private readonly ReClassNetProject project;
        private readonly Action markDirty;
        private readonly List<PatchDefinition> definitions = new List<PatchDefinition>();
        public IReadOnlyList<PatchDefinition> Definitions => definitions.Select(d => d.Clone()).ToArray();
        public bool IsReadOnly { get; private set; }
        public string Message { get; private set; }
        public event EventHandler Changed;
        public PatchRepository(ReClassNetProject project, Action markDirty = null)
        {
            this.project = project ?? throw new ArgumentNullException(nameof(project));
            this.markDirty = markDirty;
            Load();
        }
        public void Load()
        {
            definitions.Clear();
            IsReadOnly = false;
            Message = null;
            try
            {
                var root = project.CustomData.GetXElement(CustomDataKey, null);
                if (root == null) return;
                if (root.Name != "patches" || (string)root.Attribute("version") != "1")
                {
                    IsReadOnly = true;
                    Message = "Unsupported patch schema; the original custom data is preserved.";
                    return;
                }
                var loaded = root.Elements("patch").Select(Read).ToArray();
                if (loaded.Select(p => p.Id).Distinct().Count() != loaded.Length) throw new FormatException("Duplicate patch IDs.");
                definitions.AddRange(loaded);
            }
            catch (Exception e)
            {
                definitions.Clear();
                IsReadOnly = true;
                Message = "Cannot read patch definitions; the original custom data is preserved. " + e.Message;
            }
        }
        public void Save()
        {
            if (IsReadOnly) return; // Never overwrite unknown-version or malformed opaque data.
            project.CustomData.SetXElement(CustomDataKey, new XElement("patches", new XAttribute("version", "1"), definitions.Select(Write)));
        }
        public void Upsert(PatchDefinition definition)
        {
            EnsureEditable();
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (definition.Id == Guid.Empty) throw new ArgumentException("A stable patch ID is required.");
            var index = definitions.FindIndex(d => d.Id == definition.Id);
            var copy = definition.Clone();
            if (index < 0) definitions.Add(copy);
            else
            {
                copy.Revision = checked(definitions[index].Revision + 1);
                definitions[index] = copy;
            }
            PersistChange();
        }
        // The caller must restore any live patch before deleting its definition.
        public bool Delete(Guid id)
        {
            EnsureEditable();
            bool removed = definitions.RemoveAll(d => d.Id == id) != 0;
            if (removed) PersistChange();
            return removed;
        }
        public void DeleteOpaqueData()
        {
            project.CustomData.SetString(CustomDataKey, null);
            definitions.Clear();
            IsReadOnly = false;
            Message = null;
            markDirty?.Invoke();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        private void EnsureEditable()
        {
            if (IsReadOnly) throw new InvalidOperationException(Message);
        }
        private void PersistChange()
        {
            Save();
            markDirty?.Invoke();
            Changed?.Invoke(this, EventArgs.Empty);
        }
        private static XElement Write(PatchDefinition d)
        {
            var savedLocator = d.LocatorKind;
            // Session-only drafts deliberately omit live addresses and session identifiers.
            var locator = new XElement("locator", new XAttribute("kind", savedLocator));
            if (savedLocator == PatchLocatorKind.ModuleOffset) locator.Add(new XAttribute("offset", "0x" + d.Offset.ToString("X", CultureInfo.InvariantCulture)));
            if (savedLocator == PatchLocatorKind.ModulePattern) locator.Add(new XAttribute("pattern", d.Pattern ?? ""), new XAttribute("entryOffset", d.EntryOffset));
            return new XElement("patch",
                new XAttribute("id", d.Id), new XAttribute("revision", d.Revision), new XAttribute("name", d.Name ?? ""),
                new XAttribute("mode", d.Mode), new XAttribute("hookMode", d.HookMode),
                new XAttribute("platform", d.Platform ?? ""), new XAttribute("architecture", d.Architecture ?? "x64"),
                new XAttribute("sourceKind", d.SourceKind), new XAttribute("boundary", d.Boundary),
                new XElement("target", new XAttribute("module", d.ModuleName ?? ""), new XAttribute("imageSha256", d.ImageSha256 ?? "")),
                locator, new XElement("selection", new XAttribute("length", d.SelectionLength), new XAttribute("expectedBytes", Hex(d.ExpectedBytes))),
                new XElement("source", d.SourceKind == PatchSourceKind.Assembly ? d.Assembly ?? "" : Hex(d.ReplacementBytes)),
                new XElement("notes", d.Notes ?? ""));
        }
        private static PatchDefinition Read(XElement e)
        {
            var target = Required(e, "target");
            var locator = Required(e, "locator");
            var selection = Required(e, "selection");
            var d = new PatchDefinition
            {
                Id = Guid.Parse((string)e.Attribute("id")), Revision = (int?)e.Attribute("revision") ?? 1,
                Name = (string)e.Attribute("name"), Mode = Parse<PatchMode>((string)e.Attribute("mode")),
                HookMode = Parse<HookSemanticMode>((string)e.Attribute("hookMode") ?? "ReplaceSelection"),
                Platform = (string)e.Attribute("platform"), Architecture = (string)e.Attribute("architecture"),
                SourceKind = Parse<PatchSourceKind>((string)e.Attribute("sourceKind")),
                Boundary = Parse<BoundarySource>((string)e.Attribute("boundary") ?? "ExplicitOrigin"),
                ModuleName = (string)target.Attribute("module"), ImageSha256 = (string)target.Attribute("imageSha256"),
                LocatorKind = Parse<PatchLocatorKind>((string)locator.Attribute("kind")),
                Pattern = (string)locator.Attribute("pattern"), EntryOffset = (long?)locator.Attribute("entryOffset") ?? 0,
                SelectionLength = (int)selection.Attribute("length"), ExpectedBytes = Unhex((string)selection.Attribute("expectedBytes")),
                Notes = (string)e.Element("notes")
            };
            if (d.SelectionLength < 0 || d.SelectionLength > 65536 || (d.ExpectedBytes.Length != 0 && d.ExpectedBytes.Length != d.SelectionLength)) throw new FormatException("Invalid selected instruction span.");
            if (d.ExpectedBytes.Length == 0) d.ExpectedBytes = null;
            if (d.LocatorKind == PatchLocatorKind.ModuleOffset)
            {
                var offset = (string)locator.Attribute("offset");
                d.Offset = offset.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ulong.Parse(offset.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ulong.Parse(offset, CultureInfo.InvariantCulture);
            }
            if (d.SourceKind == PatchSourceKind.Assembly) d.Assembly = (string)Required(e, "source");
            else d.ReplacementBytes = Unhex((string)Required(e, "source"));
            return d;
        }
        private static XElement Required(XElement e, string name) => e.Element(name) ?? throw new FormatException("Missing " + name + ".");
        private static T Parse<T>(string value) where T : struct
        {
            T result;
            if (!Enum.TryParse(value, out result) || !Enum.IsDefined(typeof(T), result)) throw new FormatException("Invalid " + typeof(T).Name + ".");
            return result;
        }
        private static string Hex(byte[] bytes) => bytes == null ? "" : BitConverter.ToString(bytes).Replace("-", "");
        private static byte[] Unhex(string text)
        {
            text = text ?? "";
            if ((text.Length & 1) != 0 || text.Length > 131072) throw new FormatException("Invalid hex byte length.");
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return bytes;
        }
    }
}
