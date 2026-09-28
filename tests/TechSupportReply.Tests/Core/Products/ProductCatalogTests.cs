using System.IO;
using System.Linq;
using TechSupportReply.Core.Products;
using TechSupportReply.Tests.TestSupport;
using Xunit;

namespace TechSupportReply.Tests.Core.Products
{
    public class ProductCatalogTests
    {
        [Fact]
        public void Load_Missing_ReturnsDefault()
        {
            using (var tmp = new TempDir())
            {
                var catalog = ProductCatalog.Load(Path.Combine(tmp.Root, "products.json"));
                Assert.NotNull(catalog.Find("ls-dyna"));
                Assert.NotNull(catalog.Find("ansys-fluent"));
                Assert.Equal("LS-DYNA", catalog.Find("LS-DYNA").DisplayName);
                Assert.NotNull(catalog.Common);
            }
        }

        [Fact]
        public void Load_CustomFile_AddsCommonAutomatically_AndDefaultsFolder()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{\"products\":[{\"id\":\"x\",\"displayName\":\"X 제품\",\"keywords\":[\"xx\"]}]}");
                var catalog = ProductCatalog.Load(path);
                Assert.Equal(new[] { "x", ProductCatalog.CommonId }, catalog.Products.Select(p => p.Id).ToArray());
                Assert.Equal("x", catalog.Find("x").Folder);
            }
        }

        [Fact]
        public void Load_DuplicateIds_Throws()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{\"products\":[{\"id\":\"a\"},{\"id\":\"A\"}]}");
                Assert.Throws<InvalidDataException>(() => ProductCatalog.Load(path));
            }
        }

        [Fact]
        public void Load_InvalidJson_ThrowsWithFileName()
        {
            using (var tmp = new TempDir())
            {
                var path = tmp.File("products.json", "{ broken");
                var ex = Assert.Throws<InvalidDataException>(() => ProductCatalog.Load(path));
                Assert.Contains("products.json", ex.Message);
            }
        }

        [Fact]
        public void SaveThenLoad_RoundTrips()
        {
            using (var tmp = new TempDir())
            {
                var path = Path.Combine(tmp.Root, "products.json");
                ProductCatalog.CreateDefault().Save(path);
                var loaded = ProductCatalog.Load(path);
                Assert.Equal(ProductCatalog.CreateDefault().Products.Count, loaded.Products.Count);
                Assert.Contains("d3hsp", loaded.Find("ls-dyna").Keywords);
            }
        }
    }
}
