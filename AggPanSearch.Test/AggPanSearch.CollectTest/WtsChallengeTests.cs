using System;
using System.Net;
using AggPanSearch.CollectSite;
using NUnit.Framework;

namespace AggPanSearch.CollectTest
{
    /// <summary>
    /// 站点安全盾（WTS）校验令牌解析：真实 403 校验页片段可取出 _wtsjsk，
    /// 无特征的真实登录/反爬 403 页面必须返回 false（不能吞错），
    /// 以及 Cookie 的主机/路径与超长输入等边界。纯函数，不触网。
    /// </summary>
    [TestFixture]
    public class WtsChallengeTests
    {
        /// <summary>站点真实返回的 403 校验页（节选，令牌为实测值）。</summary>
        private const string RealChallengePage =
            "<html>\r\n" +
            "    <head>\r\n" +
            "        <meta http-equiv=\"content-type\" content=\"text/html; charset=UTF-8\">\r\n" +
            "    </head>\r\n" +
            "    <noscript>The browser does not support JavaScript. Cannot be accessed！</noscript>\r\n" +
            "    <body>\r\n" +
            "       <H1 id=\"msg\"></H1>\r\n" +
            "    </body>\r\n" +
            "    <script type=\"text/javascript\">\r\n" +
            "        var dt = new Date();\r\n" +
            "        dt.setSeconds(dt.getSeconds() + 60);\r\n" +
            "        document.cookie = \"_wtsjsk=0ddfd901c15d2d7e137907bdb8939cd8; path=/ \";\r\n" +
            "        var cookiesEnabled = document.cookie.indexOf(\"_wtsjsk=\") != -1;\r\n" +
            "    </script>\r\n" +
            "</html>";

        [Test]
        public void TryExtractToken_ExtractsTokenFromRealChallengePage()
        {
            bool ok = WtsChallenge.TryExtractToken(RealChallengePage, out string token);

            Assert.That(ok, Is.True);
            Assert.That(token, Is.EqualTo("0ddfd901c15d2d7e137907bdb8939cd8"));
            Assert.That(WtsChallenge.IsChallengeBody(RealChallengePage), Is.True);
        }

        [Test]
        public void TryExtractToken_ReturnsFalseForPlainLoginPage()
        {
            const string loginPage =
                "<html><head><title>403 Forbidden</title></head>" +
                "<body><h1>403</h1><p>您无权访问该页面，请先登录。</p></body></html>";

            bool ok = WtsChallenge.TryExtractToken(loginPage, out string token);

            Assert.That(ok, Is.False);
            Assert.That(token, Is.Empty);
            Assert.That(WtsChallenge.IsChallengeBody(loginPage), Is.False);
        }

        [Test]
        public void TryExtractToken_ReturnsFalseForNullOrEmptyBody()
        {
            Assert.That(WtsChallenge.TryExtractToken(null, out _), Is.False);
            Assert.That(WtsChallenge.TryExtractToken(string.Empty, out _), Is.False);
            Assert.That(WtsChallenge.TryExtractToken("   ", out _), Is.False);
            Assert.That(WtsChallenge.IsChallengeBody(null), Is.False);
        }

        [Test]
        public void TryExtractToken_ToleratesCaseWhitespaceAndQuotes()
        {
            const string page = "<script>document.cookie = \"_WTSJSK = 'ABC123DEF4567890ABCDEF1234567890' ; path=/ \"</script>";

            bool ok = WtsChallenge.TryExtractToken(page, out string token);

            Assert.That(ok, Is.True);
            Assert.That(token, Is.EqualTo("ABC123DEF4567890ABCDEF1234567890"));
        }

        [Test]
        public void TryExtractToken_IgnoresNonHexValue()
        {
            const string page = "<script>document.cookie = \"_wtsjsk=not-a-hex-token; path=/ \"</script>";

            Assert.That(WtsChallenge.TryExtractToken(page, out string token), Is.False);
            Assert.That(token, Is.Empty);
        }

        [Test]
        public void TryExtractToken_DoesNotThrowForOversizedInput()
        {
            string page = new string('a', 200 * 1024) + "_wtsjsk=0ddfd901c15d2d7e137907bdb8939cd8";

            Assert.DoesNotThrow(() => WtsChallenge.TryExtractToken(page, out _));
        }

        [Test]
        public void CreateCookie_TargetsHostRootPath()
        {
            Uri target = new Uri("http://www.duoduoziyuanku.com/page/1/");

            Cookie cookie = WtsChallenge.CreateCookie(target, "0ddfd901c15d2d7e137907bdb8939cd8");

            Assert.That(cookie.Name, Is.EqualTo(WtsChallenge.CookieName));
            Assert.That(cookie.Value, Is.EqualTo("0ddfd901c15d2d7e137907bdb8939cd8"));
            Assert.That(cookie.Domain, Is.EqualTo("www.duoduoziyuanku.com"));
            Assert.That(cookie.Path, Is.EqualTo("/"));
            Assert.That(cookie.Expires, Is.GreaterThan(DateTime.Now));
        }

        [Test]
        public void CreateCookie_CanBeReadBackFromCookieContainer()
        {
            Uri target = new Uri("http://www.duoduoziyuanku.com/post/140.html");
            CookieContainer container = new CookieContainer();

            container.Add(target, WtsChallenge.CreateCookie(target, "0ddfd901c15d2d7e137907bdb8939cd8"));

            CookieCollection cookies = container.GetCookies(target);
            Assert.That(cookies[WtsChallenge.CookieName]?.Value, Is.EqualTo("0ddfd901c15d2d7e137907bdb8939cd8"));
        }
    }
}
