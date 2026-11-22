using System.Reflection;
using AggPanSearch.CollectSite.Parsers;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 多多资源库详情页简介规则：只读正文容器（id=viewer / tzt-article_content），
    /// 不使用 meta description；容器内「资源链接」后面紧跟链接地址时才截断，
    /// 避免简介里的「如果资源链接失效了…」被误伤成只剩一个「如果」。
    /// </summary>
    [TestFixture]
    public class DuoduoResourceSiteParserIntroTests
    {
        private static string ExtractIntro(string html)
        {
            MethodInfo method = typeof(DuoduoResourceSiteParser).GetMethod(
                "ExtractDetailIntro",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            return (string)method.Invoke(null, new object[] { html })!;
        }

        private static string BuildPage(string body)
        {
            return "<html><body><div id=\"viewer\">" + body + "</div></article><footer>上一篇 下一篇 阅读：10 次</footer></body></html>";
        }

        [Test]
        public void ExtractDetailIntro_WechatSentenceIsNotTreatedAsLinkCut()
        {
            string html = BuildPage("哪吒二高清4k，点击下方链接尽快转存吧！如果资源链接失效了，可...");

            string intro = ExtractIntro(html);

            Assert.Multiple(() =>
            {
                Assert.That(intro, Does.Contain("如果资源链接失效了"));
                // 句尾的「...」属于站点截断残留，按既有清洗规则去掉
                Assert.That(intro, Does.EndWith("可"));
            });
        }

        [Test]
        public void ExtractDetailIntro_CutsRealResourceLinkSection()
        {
            string html = BuildPage("哪吒二高清4k，更新至20集。资源链接 https://pan.quark.cn/s/4275fe50f165 点击转存");

            string intro = ExtractIntro(html);

            Assert.That(intro, Is.EqualTo("哪吒二高清4k，更新至20集。"));
        }

        [Test]
        public void ExtractDetailIntro_FallsBackToTztArticleContent()
        {
            string html = "<html><body><div id=\"tzt-article_content\"><p>豫剧是中国五大戏曲剧种之一。</p></div></article></body></html>";

            Assert.That(ExtractIntro(html), Is.EqualTo("豫剧是中国五大戏曲剧种之一。"));
        }

        [Test]
        public void ExtractDetailIntro_IgnoresMetaDescription()
        {
            // 没有正文容器时，即使 meta description 有内容也不采用
            string html = "<html><head><meta name=\"description\" content=\"哪吒二高清4k，点击转存\"/></head><body><p>正文之外的模板文字</p></body></html>";

            Assert.That(ExtractIntro(html), Is.Empty);
        }

        [Test]
        public void ExtractDetailIntro_NoBodyContainer_ReturnsEmpty()
        {
            Assert.That(ExtractIntro("<html><body><p>整页纯文本</p></body></html>"), Is.Empty);
        }
    }
}
