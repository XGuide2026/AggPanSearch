using AggPanSearch.CollectSite;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 资源简介清洗：删除「链接：」「下载链接：」标签、网盘分享链接与微信引流文案，
    /// 并清理残留空行与多余标点。纯函数逻辑，用例直接断言清洗结果。
    /// </summary>
    [TestFixture]
    public class SiteCollectIntroCleanerTests
    {
        [Test]
        public void Clean_NullOrEmpty_ReturnsEmpty()
        {
            Assert.Multiple(() =>
            {
                Assert.That(SiteCollectIntroCleaner.Clean(null), Is.EqualTo(string.Empty));
                Assert.That(SiteCollectIntroCleaner.Clean("   "), Is.EqualTo(string.Empty));
            });
        }

        [Test]
        public void Clean_RemovesLinkLabels()
        {
            string result = SiteCollectIntroCleaner.Clean("链接：abcdef\n下载链接：123456");

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Not.Contain("链接："));
                Assert.That(result, Does.Not.Contain("下载链接"));
                Assert.That(result, Is.EqualTo("abcdef\n123456"));
            });
        }

        [Test]
        public void Clean_RemovesQuarkPanUrl()
        {
            string result = SiteCollectIntroCleaner.Clean(
                "分辨率 4K\nhttps://pan.quark.cn/s/e8aef260bed4");

            Assert.That(result, Is.EqualTo("分辨率 4K"));
        }

        [Test]
        public void Clean_RemovesBarePanUrlWithoutScheme()
        {
            string result = SiteCollectIntroCleaner.Clean(
                "全集更新中\npan.quark.cn/s/e8aef260bed4?pwd=1234");

            Assert.That(result, Is.EqualTo("全集更新中"));
        }

        [Test]
        public void Clean_KeepsNonNetDiskUrl()
        {
            string result = SiteCollectIntroCleaner.Clean("官网：https://www.example.com/about");

            Assert.That(result, Is.EqualTo("官网：https://www.example.com/about"));
        }

        [Test]
        public void Clean_RemovesWechatAdText()
        {
            string result = SiteCollectIntroCleaner.Clean(
                "1080P 高清\n如果资源链接失效了，可以加微信：hh1802770，重新获取！");

            Assert.That(result, Is.EqualTo("1080P 高清"));
        }

        [Test]
        public void Clean_RemovesWechatAdTextWithVariantPunctuation()
        {
            string result = SiteCollectIntroCleaner.Clean(
                "内含字幕 如果资源链接失效了,可以加微信:abc_123，重新获取!");

            Assert.That(result, Is.EqualTo("内含字幕"));
        }

        [Test]
        public void Clean_CombinedRules_KeepsOnlyDescription()
        {
            string input = "名称：庆余年 第二季\n"
                + "简介：全网独播 4K 画质\n"
                + "链接：https://pan.quark.cn/s/e8aef260bed4\n"
                + "下载链接：pan.quark.cn/s/e8aef260bed4\n"
                + "如果资源链接失效了，可以加微信：hh1802770，重新获取！";

            string result = SiteCollectIntroCleaner.Clean(input);

            Assert.That(result, Is.EqualTo("名称：庆余年 第二季\n简介：全网独播 4K 画质"));
        }

        [Test]
        public void Clean_CollapsesBlankLinesAndTrailingSymbols()
        {
            string result = SiteCollectIntroCleaner.Clean("　第一行　\n\n  \n、链接：\n第二行");

            Assert.That(result, Is.EqualTo("第一行\n第二行"));
        }

        [Test]
        public void Clean_TruncatedWechatAdAtEnd_IsRemoved()
        {
            string input = "在朋友那里拿来的哪吒二高清+4k，非常不错的电影错过了，你一定会后悔的！"
                + "点击下方链接，尽快转存吧！如果资源链接失效了，可...";

            string result = SiteCollectIntroCleaner.Clean(input);

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Not.Contain("如果"));
                Assert.That(result, Does.EndWith("尽快转存吧！"));
            });
        }

        [Test]
        public void Clean_WechatAdWithExtraSpaces_IsRemoved()
        {
            string input = "点击下方链接，尽快转存吧！如果资源链接失效了，可以加微信：  hh1802770  ，重新获取！"
                + "          链接：     https://pan.quark.cn/s/4275fe50f165    版权声明  本站不存储任何实质资源。";

            string result = SiteCollectIntroCleaner.Clean(input);

            Assert.Multiple(() =>
            {
                Assert.That(result, Does.Not.Contain("如果"));
                Assert.That(result, Does.Not.Contain("微信"));
                Assert.That(result, Does.Not.Contain("pan.quark"));
                Assert.That(result, Does.Not.Contain("链接："));
                Assert.That(result, Does.StartWith("点击下方链接，尽快转存吧！"));
            });
        }

        [Test]
        public void Clean_DanglingIfAtEnd_IsRemoved()
        {
            string result = SiteCollectIntroCleaner.Clean("点击下方链接，尽快转存吧！如果");

            Assert.That(result, Is.EqualTo("点击下方链接，尽快转存吧！"));
        }
    }
}
