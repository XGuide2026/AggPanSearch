using System.Collections.Generic;
using AggPanSearch.CollectSite;
using AggPanSearch.CollectSite.NetDisk;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 采集条目归一化：Filtering（空链接/超长/非网盘）→ 补提取码 → 跨页去重 → 字段裁剪。
    /// 该逻辑是纯函数，不触网、不读写文件，用例可直接断言计数与字段。
    /// </summary>
    [TestFixture]
    public class SiteCollectItemNormalizerTests
    {
        private const string QuarkUrl = "https://pan.quark.cn/s/abcdef123456";

        private static CollectedItem CreateItem(string url, string name = "资源", string password = "")
        {
            return new CollectedItem
            {
                Url = url,
                Name = name,
                Password = password,
                CloudType = CloudTypes.Quark,
            };
        }

        [Test]
        public void Normalize_DuplicateUrlAcrossBatches_IsSkipped()
        {
            HashSet<string> seen = new HashSet<string>();

            SiteCollectItemNormalizeResult first = SiteCollectItemNormalizer.Normalize(new[] { CreateItem(QuarkUrl) }, seen, 100);
            SiteCollectItemNormalizeResult second = SiteCollectItemNormalizer.Normalize(new[] { CreateItem(QuarkUrl) }, seen, 100);

            Assert.Multiple(() =>
            {
                Assert.That(first.Items, Has.Count.EqualTo(1));
                Assert.That(first.Skipped, Is.EqualTo(0));
                Assert.That(second.Items, Is.Empty);
                Assert.That(second.Skipped, Is.EqualTo(1));
            });
        }

        [Test]
        public void Normalize_EmptyUrl_IsSkipped()
        {
            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(string.Empty) }, new HashSet<string>(), 100);

            Assert.Multiple(() =>
            {
                Assert.That(result.Items, Is.Empty);
                Assert.That(result.Skipped, Is.EqualTo(1));
            });
        }

        [Test]
        public void Normalize_OverlongUrl_IsSkipped()
        {
            string longUrl = "https://pan.quark.cn/s/" + new string('a', SiteCollectItemNormalizer.MaxUrlLength + 10);

            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(longUrl) }, new HashSet<string>(), 100);

            Assert.Multiple(() =>
            {
                Assert.That(result.Items, Is.Empty);
                Assert.That(result.Skipped, Is.EqualTo(1));
            });
        }

        [Test]
        public void Normalize_UnknownCloudType_IsSkipped()
        {
            CollectedItem item = new CollectedItem
            {
                Url = "https://example.com/article/1",
                Name = "普通网页",
            };

            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { item }, new HashSet<string>(), 100);

            Assert.Multiple(() =>
            {
                Assert.That(result.Items, Is.Empty);
                Assert.That(result.Skipped, Is.EqualTo(1));
            });
        }

        [Test]
        public void Normalize_AppendsPassword_WhenUrlHasNone()
        {
            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(QuarkUrl, password: "1234") }, new HashSet<string>(), 100);

            Assert.That(result.Items, Has.Count.EqualTo(1));
            Assert.That(result.Items[0].Url, Does.Contain("pwd=1234"));
        }

        [Test]
        public void Normalize_KeepsExistingPassword()
        {
            string urlWithPwd = QuarkUrl + "?pwd=9999";

            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(urlWithPwd, password: "1234") }, new HashSet<string>(), 100);

            Assert.That(result.Items, Has.Count.EqualTo(1));
            Assert.That(result.Items[0].Url, Does.Contain("pwd=9999"));
            Assert.That(result.Items[0].Url, Does.Not.Contain("pwd=1234"));
        }

        [Test]
        public void Normalize_TruncatesOverlongName()
        {
            string longName = new string('名', SiteCollectItemNormalizer.MaxNameLength + 100);

            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(QuarkUrl, name: longName) }, new HashSet<string>(), 100);

            Assert.That(result.Items[0].Name.Length, Is.EqualTo(SiteCollectItemNormalizer.MaxNameLength));
        }

        [Test]
        public void Normalize_EmptyName_FallsBackToPlaceholder()
        {
            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(
                new[] { CreateItem(QuarkUrl, name: "   ") }, new HashSet<string>(), 100);

            Assert.That(result.Items[0].Name, Is.EqualTo("未命名资源"));
        }

        [Test]
        public void Normalize_RespectsMaxItems()
        {
            List<CollectedItem> items = new List<CollectedItem>();
            for (int i = 0; i < 5; i++)
            {
                items.Add(CreateItem(QuarkUrl + i));
            }

            SiteCollectItemNormalizeResult result = SiteCollectItemNormalizer.Normalize(items, new HashSet<string>(), 2);

            Assert.That(result.Items, Has.Count.EqualTo(2));
        }

        [Test]
        public void NormalizeName_CollapsesWhitespace()
        {
            Assert.That(SiteCollectItemNormalizer.NormalizeName("  三体 \r\n 全集  "), Is.EqualTo("三体 全集"));
        }

        [Test]
        public void AppendPassword_WithoutPassword_ReturnsOriginalUrl()
        {
            Assert.That(SiteCollectItemNormalizer.AppendPassword(QuarkUrl, "  "), Is.EqualTo(QuarkUrl));
        }
    }
}
