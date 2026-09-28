using System.Linq;
using System.Text;
using TechSupportReply.Rag.Loaders;
using Xunit;

namespace TechSupportReply.Tests.Rag.Loaders
{
    public class TextFileReaderTests
    {
        private const string Korean = "접촉 관통 경고";

        [Fact]
        public void Decode_Utf8Bom()
        {
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Korean)).ToArray();
            Assert.Equal(Korean, TextFileReader.Decode(bytes));
        }

        [Fact]
        public void Decode_Utf8NoBom()
        {
            Assert.Equal(Korean, TextFileReader.Decode(Encoding.UTF8.GetBytes(Korean)));
        }

        [Fact]
        public void Decode_Utf16LeBom()
        {
            var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Korean)).ToArray();
            Assert.Equal(Korean, TextFileReader.Decode(bytes));
        }

        [Fact]
        public void Decode_Cp949()
        {
            TextFileReader.EnsureCodePages();
            var bytes = Encoding.GetEncoding(949).GetBytes(Korean);
            Assert.Equal(Korean, TextFileReader.Decode(bytes));
        }

        [Fact]
        public void Decode_Empty()
        {
            Assert.Equal("", TextFileReader.Decode(new byte[0]));
        }
    }
}
