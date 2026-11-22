using System;
using System.Collections.Generic;
using System.Linq;
using AggPanSearch.CollectSite;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 正文多图提取：按页面顺序去重、最多 5 张、懒加载属性、主题 logo/小图标过滤，
    /// 以及入库拼接「超长整条丢弃、不产生半截地址」。
    /// 该逻辑是纯函数，不触网、不读写文件。
    /// </summary>
    [TestFixture]
    public class SiteCollectHtmlUtilImageTests
    {
        private static readonly Uri PageUri = new Uri("http://www.duoduoziyuanku.com/post/140.html");

        private static string ImageTag(string src)
        {
            return "<p><img src=\"" + src + "\" /></p>";
        }

        [Test]
        public void ExtractImages_KeepsPageOrderAndDeduplicates()
        {
            string html =
                "<div id=\"viewer\">" +
                ImageTag("/zb_users/upload/2026/01/a.jpg") +
                ImageTag("/zb_users/upload/2026/01/b.png") +
                ImageTag("/zb_users/upload/2026/01/a.jpg") +
                "</div>";

            List<string> images = SiteCollectHtmlUtil.ExtractImages(html, PageUri);

            Assert.That(images, Is.EqualTo(new[]
            {
                "http://www.duoduoziyuanku.com/zb_users/upload/2026/01/a.jpg",
                "http://www.duoduoziyuanku.com/zb_users/upload/2026/01/b.png",
            }));
        }

        [Test]
        public void ExtractImages_LimitsToFiveImages()
        {
            string html = "<div id=\"viewer\">";
            for (int i = 0; i < 8; i++)
            {
                html += ImageTag("/zb_users/upload/2026/01/" + i + ".jpg");
            }
            html += "</div>";

            List<string> images = SiteCollectHtmlUtil.ExtractImages(html, PageUri);

            Assert.That(images, Has.Count.EqualTo(SiteCollectHtmlUtil.MaxImageCount));
            Assert.That(images[0], Does.EndWith("/0.jpg"));
            Assert.That(images[4], Does.EndWith("/4.jpg"));
        }

        [Test]
        public void ExtractImages_SupportsLazyLoadAttributes()
        {
            string html =
                "<div id=\"viewer\">" +
                "<img src=\"/static/loading.gif\" data-original=\"/zb_users/upload/2026/01/lazy1.jpg\" />" +
                "<img src=\"data:image/gif;base64,R0lGOD\" data-src=\"/zb_users/upload/2026/01/lazy2.png\" />" +
                "</div>";

            List<string> images = SiteCollectHtmlUtil.ExtractImages(html, PageUri);

            Assert.That(images, Has.Count.EqualTo(2));
            Assert.That(images[0], Does.EndWith("/lazy1.jpg"));
            Assert.That(images[1], Does.EndWith("/lazy2.png"));
        }

        [Test]
        public void ExtractImages_FiltersThemeLogoAndIcons()
        {
            string html =
                "<div id=\"viewer\">" +
                ImageTag("/zb_users/theme/tzt/style/images/logo.png") +
                ImageTag("/zb_users/upload/2026/01/qrcode.png") +
                ImageTag("/zb_users/upload/2026/01/avatar.png") +
                ImageTag("/zb_users/upload/2026/01/real.jpg") +
                "</div>";

            List<string> images = SiteCollectHtmlUtil.ExtractImages(html, PageUri);

            Assert.That(images, Has.Count.EqualTo(1));
            Assert.That(images[0], Does.EndWith("/real.jpg"));
        }

        [Test]
        public void ExtractImages_SkipsExplicitSmallImages()
        {
            string html =
                "<div id=\"viewer\">" +
                "<img src=\"/zb_users/upload/2026/01/small.png\" width=\"24\" height=\"24\" />" +
                ImageTag("/zb_users/upload/2026/01/big.jpg") +
                "</div>";

            List<string> images = SiteCollectHtmlUtil.ExtractImages(html, PageUri);

            Assert.That(images, Has.Count.EqualTo(1));
            Assert.That(images[0], Does.EndWith("/big.jpg"));
        }

        [Test]
        public void ExtractOgImage_PrefersUsableMetaImage()
        {
            string html =
                "<meta property=\"og:image\" content=\"/zb_users/theme/tzt/style/images/logo.png\" />" +
                "<meta property=\"og:image\" content=\"/zb_users/upload/2026/01/cover.jpg\" />";

            Assert.That(
                SiteCollectHtmlUtil.ExtractOgImage(html, PageUri),
                Is.EqualTo("http://www.duoduoziyuanku.com/zb_users/upload/2026/01/cover.jpg"));
        }

        [Test]
        public void NormalizeImageUrl_EscapesSemicolonAndRejectsNonImage()
        {
            Assert.That(
                SiteCollectHtmlUtil.NormalizeImageUrl(PageUri, "/zb_users/upload/a;b.jpg"),
                Is.EqualTo("http://www.duoduoziyuanku.com/zb_users/upload/a%3Bb.jpg"));
            Assert.That(SiteCollectHtmlUtil.NormalizeImageUrl(PageUri, "/zb_users/theme/tzt/style.css"), Is.Empty);
            Assert.That(SiteCollectHtmlUtil.NormalizeImageUrl(PageUri, "javascript:alert(1)"), Is.Empty);
        }

        [Test]
        public void JoinImages_DropsWholeOverlongImageInsteadOfCutting()
        {
            string first = "http://www.duoduoziyuanku.com/zb_users/upload/2026/01/" + new string('a', 480) + ".jpg";
            string second = "http://www.duoduoziyuanku.com/zb_users/upload/2026/01/" + new string('b', 600) + ".jpg";

            string joined = SiteCollectHtmlUtil.JoinImages(new[] { first, second }, 1000);

            Assert.That(joined, Is.EqualTo(first));
            Assert.That(joined, Does.Not.Contain("bbb"));
        }

        [Test]
        public void JoinImages_LimitsCountToFive()
        {
            List<string> images = Enumerable.Range(0, 7)
                .Select(i => "http://www.duoduoziyuanku.com/zb_users/upload/2026/01/" + i + ".jpg")
                .ToList();

            string joined = SiteCollectHtmlUtil.JoinImages(images);

            Assert.That(SiteCollectHtmlUtil.SplitImages(joined), Has.Count.EqualTo(SiteCollectHtmlUtil.MaxImageCount));
        }

        [Test]
        public void SetImages_SyncsImageUrlAndTrimsToFive()
        {
            CollectedItem item = new CollectedItem();
            item.SetImages(new[]
            {
                " http://a.com/1.jpg ", "http://a.com/2.jpg", "http://a.com/1.jpg",
                "http://a.com/3.jpg", "http://a.com/4.jpg", "http://a.com/5.jpg", "http://a.com/6.jpg",
            });

            Assert.That(item.ImageUrls, Has.Count.EqualTo(5));
            Assert.That(item.ImageUrl, Is.EqualTo(string.Join(";", item.ImageUrls)));
        }
    }
}
