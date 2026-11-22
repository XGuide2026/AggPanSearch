using AggPanSearch.CollectSite;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 分页链接模板规则：校验、按页码构建地址（尾斜杠正则化）、由列表页地址推导起始页码。
    /// 这些逻辑被视图预检、站点校验与引擎翻页共用，任何一处改动都必须先过这组用例。
    /// </summary>
    [TestFixture]
    public class SiteCollectPageTemplateTests
    {
        [Test]
        public void TryParse_EmptyTemplate_ReturnsError()
        {
            bool ok = SiteCollectPageTemplate.TryParse("   ", out _, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(error, Does.Contain("分页链接为空"));
            });
        }

        [Test]
        public void TryParse_WithoutPlaceholder_ReturnsError()
        {
            bool ok = SiteCollectPageTemplate.TryParse("https://example.com/page/", out _, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(error, Does.Contain("{page}"));
            });
        }

        [Test]
        public void TryParse_NonHttpTemplate_ReturnsError()
        {
            bool ok = SiteCollectPageTemplate.TryParse("ftp://example.com/page/{page}", out _, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(error, Does.Contain("http://"));
            });
        }

        [Test]
        public void TryParse_PlaceholderIsCaseInsensitive()
        {
            bool ok = SiteCollectPageTemplate.TryParse("https://example.com/page/{PAGE}", out SiteCollectPageTemplate template, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True, error);
                Assert.That(template.BuildUrl(3), Is.EqualTo("https://example.com/page/3/"));
            });
        }

        [Test]
        public void BuildUrl_LastSegmentTemplate_AppendsTrailingSlash()
        {
            SiteCollectPageTemplate.TryParse("https://www.duoduoziyuanku.com/page/{page}", out SiteCollectPageTemplate template, out _);

            Assert.Multiple(() =>
            {
                Assert.That(template.CanNormalizeSlash, Is.True);
                Assert.That(template.BuildUrl(2), Is.EqualTo("https://www.duoduoziyuanku.com/page/2/"));
                Assert.That(template.BuildUrl(2, false), Is.EqualTo("https://www.duoduoziyuanku.com/page/2"));
            });
        }

        [Test]
        public void BuildUrl_TemplateWithTrailingSlash_IsNotDoubled()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}/", out SiteCollectPageTemplate template, out _);

            Assert.That(template.BuildUrl(4), Is.EqualTo("https://example.com/page/4/"));
        }

        [Test]
        public void BuildUrl_QueryTemplate_ReplacesPlaceholderOnly()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/list?page={page}", out SiteCollectPageTemplate template, out _);

            Assert.Multiple(() =>
            {
                Assert.That(template.CanNormalizeSlash, Is.False);
                Assert.That(template.BuildUrl(3), Is.EqualTo("https://example.com/list?page=3"));
            });
        }

        [Test]
        public void BuildUrl_SuffixTemplate_KeepsSuffix()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/tag/{page}.html", out SiteCollectPageTemplate template, out _);

            Assert.Multiple(() =>
            {
                Assert.That(template.CanNormalizeSlash, Is.False);
                Assert.That(template.BuildUrl(5), Is.EqualTo("https://example.com/tag/5.html"));
            });
        }

        [Test]
        public void ExampleUrl_IsBuiltFromPageOne()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}", out SiteCollectPageTemplate template, out _);

            Assert.That(template.ExampleUrl, Is.EqualTo("https://example.com/page/1/"));
        }

        [Test]
        public void TryResolvePageNo_EmptyListUrl_StartsFromFirstPage()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("", "https://example.com/", out int pageNo, out _);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True);
                Assert.That(pageNo, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryResolvePageNo_SameAsSiteUrl_StartsFromFirstPage()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("https://example.com", "https://example.com/", out int pageNo, out _);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True);
                Assert.That(pageNo, Is.EqualTo(1));
            });
        }

        [Test]
        public void TryResolvePageNo_ParsesPageNo_IgnoringSchemeWwwAndTrailingSlash()
        {
            SiteCollectPageTemplate.TryParse("https://www.duoduoziyuanku.com/page/{page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("http://duoduoziyuanku.com/page/5/", "https://www.duoduoziyuanku.com/", out int pageNo, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True, error);
                Assert.That(pageNo, Is.EqualTo(5));
            });
        }

        [Test]
        public void TryResolvePageNo_QueryTemplate_ParsesPageNo()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/list?page={page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("https://example.com/list?page=7", "https://example.com/", out int pageNo, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.True, error);
                Assert.That(pageNo, Is.EqualTo(7));
            });
        }

        [Test]
        public void TryResolvePageNo_Mismatch_ErrorContainsExampleUrl()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("https://example.com/other/5", "https://example.com/", out int pageNo, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(pageNo, Is.EqualTo(1));
                Assert.That(error, Does.Contain("不匹配"));
                Assert.That(error, Does.Contain(template.ExampleUrl));
            });
        }

        [Test]
        public void TryResolvePageNo_OutOfRangePageNo_IsRejected()
        {
            SiteCollectPageTemplate.TryParse("https://example.com/page/{page}", out SiteCollectPageTemplate template, out _);

            bool ok = template.TryResolvePageNo("https://example.com/page/999999", "https://example.com/", out _, out string error);

            Assert.Multiple(() =>
            {
                Assert.That(ok, Is.False);
                Assert.That(error, Does.Contain("不匹配"));
            });
        }
    }
}
