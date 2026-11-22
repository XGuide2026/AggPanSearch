/*!
 * 公众号引流弹窗（后台：网盘管理 - 引流设置）
 * 样式见 wwwroot/userfrontend/css/wx-traffic.css
 *
 * 前台：_Layout.cshtml 输出 window.SITE_WX_TRAFFIC = { searchMode, viewSourceMode, qr, code }，
 *       本文件加载后自动初始化，并对外暴露：
 *         WxTraffic.guard(mode)            搜索页放行判断（mode 1=搜索资源 / 2=查看资源）
 *         WxTraffic.viewSourceGuard(mode)  详情页放行判断
 *         WxTraffic.preview({qr, code})    后台预览弹窗（不写激活状态）
 *         WxTraffic.reset()                清除已激活记忆
 *       同时保留旧入口 window.wxTrafficGuard / window.wxTrafficViewSourceGuard / window.wxTrafficReset。
 */
(function (window, document) {
    'use strict';

    var ACTIVE_KEY = 'wx_traffic_activated';
    var POPUP_ID = 'wx-traffic-popup';

    var cfg = null;
    var pendingCallbacks = [];
    var linkGuardBound = false;

    function trim(v) {
        return v == null ? '' : String(v).trim();
    }

    function esc(v) {
        return String(v == null ? '' : v)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // 后台存的是不带前导斜杠的相对路径（Resource/xxx），统一补齐，避免子页面相对路径错位
    function normalizeQr(url) {
        var v = trim(url);
        if (!v) {
            return '';
        }
        if (v.charAt(0) === '/' || /^https?:\/\//i.test(v) || /^data:/i.test(v)) {
            return v;
        }
        return '/' + v;
    }

    /**
     * 把主题变量写到弹窗容器上
     * 前台页面 :root 已有 --theme-* 变量，直接继承即可；
     * 后台预览页面没有这些变量，由调用方传入（key 可带或不带 "--" 前缀）
     */
    function applyTheme(el, vars) {
        if (!el || !el.style || !vars) {
            return;
        }
        for (var k in vars) {
            if (!Object.prototype.hasOwnProperty.call(vars, k)) {
                continue;
            }
            var name = String(k).indexOf('--') === 0 ? k : '--' + k;
            var val = trim(vars[k]);
            if (val) {
                el.style.setProperty(name, val);
            }
        }
    }

    function normalize(raw) {
        raw = raw || {};
        return {
            searchMode: parseInt(raw.searchMode, 10) || 0,
            viewSourceMode: parseInt(raw.viewSourceMode, 10) || 0,
            qr: normalizeQr(raw.qr),
            // 公众号名称（后台配置 traffic_wxchannel_name），未配置时步骤里只提二维码
            channel: trim(raw.channel),
            code: trim(raw.code)
        };
    }

    // 激活码：后台支持「、」/逗号/分号/空格分隔多个，命中任意一个即通过
    function getCodeList(code) {
        var parts = trim(code).split(/[、,，;；\s]+/);
        var list = [];
        for (var i = 0; i < parts.length; i++) {
            var v = trim(parts[i]);
            if (v) {
                list.push(v.toUpperCase());
            }
        }
        return list;
    }

    function isActivated() {
        try { return localStorage.getItem(ACTIVE_KEY) === '1'; } catch (e) { return false; }
    }

    function markActivated() {
        try { localStorage.setItem(ACTIVE_KEY, '1'); } catch (e) { }
    }

    function reset() {
        try { localStorage.removeItem(ACTIVE_KEY); } catch (e) { }
    }

    function init(config) {
        cfg = normalize(config || window.SITE_WX_TRAFFIC || {});
        bindDetailLinkGuard();
        return cfg;
    }

    function getCfg() {
        if (!cfg) {
            cfg = normalize(window.SITE_WX_TRAFFIC || {});
        }
        return cfg;
    }

    /**
     * 打开弹窗
     * @param {Object} options { cfg, preview, onSuccess }
     *   cfg      弹窗配置（qr / code），缺省取站点配置
     *   preview  后台预览模式：不写激活状态，点遮罩可关闭，验证通过后提示并自动关闭
     *   onSuccess 验证通过后的回调
     */
    function open(options) {
        options = options || {};
        var popupCfg = normalize(options.cfg || getCfg());
        var preview = !!options.preview;

        // 弹窗打开期间可能有多个拦截请求（如连续点击），先登记回调，验证通过后一起放行
        if (typeof options.onSuccess === 'function') {
            pendingCallbacks.push(options.onSuccess);
        }
        if (document.getElementById(POPUP_ID)) {
            return null;
        }

        var codes = getCodeList(popupCfg.code);
        var wrap = document.createElement('div');
        wrap.id = POPUP_ID;

        var h = '<div class="wx-traffic-mask"></div>';
        h += '<div class="wx-traffic-dialog">';
        if (preview) {
            h += '<span class="wx-traffic-badge">后台预览</span>';
        }
        h += '<div class="wx-traffic-title">人机验证提示</div>';
        // 二维码上方的提示（居中，每段独立一行）
        h += '<div class="wx-traffic-notice">';
        h += '<p>为了避免高频爬虫、CC攻击的困扰，搜索需要人机验证！</p>';
        h += '<p>🔥只需激活一次、不取关、永久有效、希望理解🔥</p>';
        h += '</div>';
        // 公众号图片取「二维码设置 - qr_wxchannel」；没有配置就整块不渲染（不显示图片，也不显示占位）
        if (popupCfg.qr) {
            h += '<div class="wx-traffic-qr"><img src="' + esc(popupCfg.qr) + '" alt="公众号二维码" onerror="this.parentNode.style.display=\'none\'" /></div>';
        }
        // 二维码下方的操作步骤（左对齐，每步独立一行）
        h += '<div class="wx-traffic-steps">';
        h += '<p>1️⃣ 微信扫描二维码👆'
            + (popupCfg.channel ? '，或者搜索公众号：<code>' + esc(popupCfg.channel) + '</code>' : '')
            + '</p>';
        h += '<p>2️⃣ 在公众号中发送：<code>验证码</code>，获取<code>验证码</code></p>';
        h += '<p>3️⃣ 复制<code>验证码</code>4位纯数字，粘贴至下方输入框中，点击「立即激活」</p>';
        h += '</div>';
        if (codes.length > 0) {
            h += '<input class="wx-traffic-input" type="text" autocomplete="off" placeholder="请输入公众号回复的验证码" />';
            h += '<div class="wx-traffic-tip"></div>';
            h += '<div class="wx-traffic-btn">立即激活</div>';
        } else {
            h += '<div class="wx-traffic-btn">我已关注，继续</div>';
        }
        h += '</div>';

        wrap.innerHTML = h;
        document.body.appendChild(wrap);
        // 主题适配：传入主题变量时（后台预览）写到弹窗容器，前台沿用页面 :root 的主题变量
        applyTheme(wrap, options.theme);

        var btn = wrap.querySelector('.wx-traffic-btn');
        var input = wrap.querySelector('.wx-traffic-input');
        var tip = wrap.querySelector('.wx-traffic-tip');

        var escHandler = null;
        function close() {
            if (escHandler) {
                document.removeEventListener('keydown', escHandler, false);
                escHandler = null;
            }
            if (wrap.parentNode) {
                wrap.parentNode.removeChild(wrap);
            }
        }

        // 验证通过：前台写入激活记忆并放行所有等待中的回调；预览只关闭弹窗
        function finish() {
            if (!preview) {
                markActivated();
            }
            close();
            var cbs = pendingCallbacks.slice();
            pendingCallbacks = [];
            for (var i = 0; i < cbs.length; i++) {
                if (typeof cbs[i] === 'function') {
                    cbs[i]();
                }
            }
        }

        function setTip(text, ok) {
            if (!tip) {
                return;
            }
            tip.className = ok ? 'wx-traffic-tip is-ok' : 'wx-traffic-tip';
            tip.textContent = text;
        }

        function submit() {
            if (codes.length > 0) {
                var val = input ? trim(input.value) : '';
                if (!val) {
                    setTip('请输入验证码', false);
                    return;
                }
                // 多个验证码中命中任意一个即通过（忽略大小写与首尾空格）
                if (codes.indexOf(val.toUpperCase()) < 0) {
                    setTip('验证码不正确，请重新输入', false);
                    return;
                }
                if (preview) {
                    setTip('验证码校验通过，前台验证后将放行资源', true);
                    setTimeout(finish, 900);
                    return;
                }
            }
            finish();
        }

        btn.addEventListener('click', submit);
        if (input) {
            input.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') {
                    submit();
                }
            });
            setTimeout(function () { try { input.focus(); } catch (e) { } }, 100);
        }

        // 预览模式：点遮罩 / 按 ESC 关闭，避免遮住后台无法操作
        if (preview) {
            wrap.querySelector('.wx-traffic-mask').addEventListener('click', close);
            escHandler = function (e) {
                if (e.key === 'Escape' || e.keyCode === 27) {
                    close();
                }
            };
            document.addEventListener('keydown', escHandler, false);
        }
        return wrap;
    }

    // 放行判断：未开启 / 未配置二维码 / 已激活 都直接放行，避免功能被卡死
    function guard(modeValue, mode) {
        return new Promise(function (resolve) {
            var pureMode = parseInt(modeValue || 0, 10) || 0;
            if (pureMode === 0 || pureMode !== mode) { resolve(true); return; }
            var c = getCfg();
            if (!c.qr) { resolve(true); return; }
            if (isActivated()) { resolve(true); return; }
            open({ cfg: c, onSuccess: function () { resolve(true); } });
        });
    }

    function searchGuard(mode) {
        return guard(getCfg().searchMode, mode);
    }

    function viewSourceGuard(mode) {
        return guard(getCfg().viewSourceMode, mode);
    }

    // viewSourceMode=1：拦截站内资源详情链接（/d/xxx.html）点击，先弹窗，验证通过后再跳转
    function bindDetailLinkGuard() {
        if (linkGuardBound || getCfg().viewSourceMode !== 1) {
            return;
        }
        linkGuardBound = true;
        document.addEventListener('click', function (e) {
            if (isActivated()) {
                return;
            }
            var el = e.target;
            while (el && el.nodeType === 1 && el.tagName !== 'A') {
                el = el.parentNode;
            }
            if (!el || el.nodeType !== 1 || el.tagName !== 'A') {
                return;
            }
            var href = el.getAttribute('href') || '';
            if (!/^\/d\/[^\/?#]+\.html/.test(href) && !/\/d\/[^\/?#]+\.html/.test(href)) {
                return;
            }
            if (el.getAttribute('data-wx-pass') === '1') {
                return;
            }
            e.preventDefault();
            e.stopPropagation();
            viewSourceGuard(1).then(function (ok) {
                if (!ok) {
                    return;
                }
                el.setAttribute('data-wx-pass', '1');
                window.location.href = href;
            });
        }, true);
    }

    window.WxTraffic = {
        init: init,
        open: open,
        // 后台「引流设置」预览：按传入的二维码 / 激活码渲染真实弹窗
        preview: function (opts) {
            opts = opts || {};
            return open({
                cfg: { qr: opts.qr, channel: opts.channel, code: opts.code },
                theme: opts.theme,
                preview: true
            });
        },
        guard: searchGuard,
        viewSourceGuard: viewSourceGuard,
        isActivated: isActivated,
        reset: reset
    };

    // 兼容旧调用入口（搜索页 / 详情页仍在使用）
    window.wxTrafficGuard = searchGuard;
    window.wxTrafficViewSourceGuard = viewSourceGuard;
    window.wxTrafficReset = reset;

    // 自动初始化：读取 _Layout.cshtml 输出的站点配置
    init(window.SITE_WX_TRAFFIC || null);
})(window, document);
