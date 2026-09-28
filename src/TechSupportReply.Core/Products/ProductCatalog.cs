using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TechSupportReply.Core.IO;
using TechSupportReply.Core.Serialization;

namespace TechSupportReply.Core.Products
{
    public sealed class ProductCatalog
    {
        public const string CommonId = "_common";
        public const string PromptFileName = "_prompt.md";

        private readonly List<ProductDefinition> _products;

        public ProductCatalog(IEnumerable<ProductDefinition> products)
        {
            _products = new List<ProductDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in products)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) throw new InvalidDataException("제품 id가 비어 있습니다.");
                if (!seen.Add(p.Id)) throw new InvalidDataException($"제품 id가 중복되었습니다: {p.Id}");
                if (string.IsNullOrWhiteSpace(p.Folder)) p.Folder = p.Id;
                if (string.IsNullOrWhiteSpace(p.DisplayName)) p.DisplayName = p.Id;
                if (p.Keywords == null) p.Keywords = new List<string>();
                _products.Add(p);
            }
            if (!seen.Contains(CommonId)) _products.Add(CommonDefinition());
        }

        public IReadOnlyList<ProductDefinition> Products => _products;

        public ProductDefinition Common => Find(CommonId);

        public ProductDefinition Find(string id) =>
            string.IsNullOrEmpty(id) ? null : _products.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

        public static ProductCatalog Load(string productsJsonPath)
        {
            if (!File.Exists(productsJsonPath)) return CreateDefault();
            ProductCatalogFile file;
            try
            {
                file = JsonSerializer.Deserialize<ProductCatalogFile>(File.ReadAllText(productsJsonPath, Encoding.UTF8), JsonDefaults.Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"products.json 형식 오류: {ex.Message}", ex);
            }
            if (file?.Products == null || file.Products.Count == 0) return CreateDefault();
            return new ProductCatalog(file.Products);
        }

        public void Save(string path) =>
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new ProductCatalogFile { Products = _products }, JsonDefaults.Options));

        public static ProductCatalog CreateDefault() => new ProductCatalog(new[]
        {
            Def("ls-dyna", "LS-DYNA", "LS-DYNA",
                "ls-dyna", "lsdyna", "ls dyna", "*keyword", "*contact", "*mat_", "*section", "*control_", "*database_",
                "*boundary_", "d3plot", "d3hsp", "messag", "binout", "mpp", "smp"),
            Def("ls-prepost", "LS-PrePost / LS-OPT", "LS-PrePost",
                "ls-prepost", "lsprepost", "lspp", "ls-opt", "lsopt"),
            Def("ansys-mechanical", "Ansys Mechanical", "Ansys-Mechanical",
                "ansys mechanical", "mechanical", "mapdl", "apdl", "workbench", "static structural",
                "transient structural", "modal", "harmonic response", "solve.out", "ds.dat"),
            Def("ansys-fluent", "Ansys Fluent", "Ansys-Fluent",
                "fluent", "udf", "fluent meshing", "residual", "under-relaxation", ".cas", ".dat.h5", "수렴", "발산"),
            Def("ansys-cfx", "Ansys CFX", "Ansys-CFX",
                "cfx", "cfx-pre", "cfd-post", "cfx-solver", ".def", ".res"),
            Def("ansys-electronics", "Ansys Electronics (HFSS/Maxwell)", "Ansys-Electronics",
                "hfss", "maxwell", "aedt", "electronics desktop", "q3d", "siwave", "icepak"),
            Def("ansys-spaceclaim", "Ansys SpaceClaim / Discovery", "Ansys-SpaceClaim",
                "spaceclaim", "discovery", ".scdoc", "geometry repair"),
            CommonDefinition(),
        });

        private static ProductDefinition CommonDefinition() =>
            Def(CommonId, "공통 (라이선스/설치)", "_common",
                "license", "licensing", "라이선스", "라이센스", "ansyslmd", "lmutil", "lmgrd", "flexnet", "flexlm",
                "license manager", "설치", "install");

        private static ProductDefinition Def(string id, string name, string folder, params string[] keywords) =>
            new ProductDefinition { Id = id, DisplayName = name, Folder = folder, Keywords = keywords.ToList() };
    }
}
