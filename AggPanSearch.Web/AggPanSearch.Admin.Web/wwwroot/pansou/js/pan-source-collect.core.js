/*
 * 站点采集 - 公共层
 * 职责：页面配置读取、模块间共享状态、通用渲染片段、地址与分页模板的前端预检。
 * 说明：接口地址由视图输出的 window.PanSourceCollectApi 提供，静态文件里不出现 Razor 表达式。
 */
(function (window, $) {
    'use strict';

    var ns = window.PanSourceCollect = window.PanSourceCollect || {};
    var api = window.PanSourceCollectApi || {};

    ns.api = api;

    // 采集模式：0 详情页采集 / 1 单个列表页采集 / 2 分页列表采集
    ns.MODE_NAMES = {
        0: '详情页采集',
        1: '单个列表页采集',
        2: '分页列表采集'
    };

    ns.MODE_HINTS = {
        0: '详情页采集：只抓取「详情页地址」这一条资源，不进入列表页。',
        1: '单个列表页采集：抓取「列表页地址」这一页，并进入该页所有详情页取链接。',
        2: '分页列表采集：按「分页链接」的 {page} 占位符逐页采集，从「列表页地址」对应的页码开始。'
    };

    // 跨模块共享的页面状态（站点列表、当前模式、运行中的任务）
    ns.state = {
        sites: [],
        mode: 1,
        running: false,
        jobId: '',
        jobKey: 'panSourceCollectJobId',
        modeKey: 'panSourceCollectMode'
    };

    // 采集内容可能来自外部站点，统一转义后再拼接，避免 XSS
    ns.escHtml = function (value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    };

    // 长文本截断显示，完整内容放在 title 中
    ns.renderEllipsis = function (value, maxLength) {
        if (ys.isNullOrEmpty(value)) {
            return '';
        }
        var text = String(value);
        var display = text.length > maxLength ? text.substring(0, maxLength) + '...' : text;
        return '<span title="' + ns.escHtml(text) + '">' + ns.escHtml(display) + '</span>';
    };

    ns.renderTime = function (value) {
        if (ys.isNullOrEmpty(value)) {
            return '';
        }
        return String(value).replace('T', ' ').substring(0, 19);
    };

    // 图片列：多图时显示首图并在右下角标注数量，title 列出全部地址，点击新窗口打开首图
    // 无图时输出与 44×44 缩略图同尺寸的占位块，避免行高与有图行不一致
    ns.renderImage = function (value) {
        if (ys.isNullOrEmpty(value)) {
            return '<span class="grid-img-placeholder">无图</span>';
        }
        var images = [];
        $.each(String(value).split(';'), function (index, item) {
            var url = $.trim(item);
            if (url !== '') {
                images.push(url);
            }
        });
        if (images.length === 0) {
            return '<span class="grid-img-placeholder">无图</span>';
        }
        var first = ns.escHtml(images[0]);
        var badge = images.length > 1
            ? '<span style="position:absolute;right:-4px;bottom:-4px;background:#3498db;color:#fff;border-radius:8px;' +
              'padding:0 4px;font-size:11px;line-height:14px;">' + images.length + '</span>'
            : '';
        return '<a href="' + first + '" target="_blank" rel="noopener noreferrer" ' +
            'style="position:relative;display:inline-block;" title="' + ns.escHtml(images.join('\n')) + '">' +
            '<img src="' + first + '" style="width:44px;height:44px;object-fit:cover;border:1px solid #eee;border-radius:4px;" ' +
            'onerror="this.style.display=\'none\'" />' + badge + '</a>';
    };

    ns.renderLink = function (value) {
        if (ys.isNullOrEmpty(value)) {
            return '';
        }
        var url = String(value);
        var display = url.length > 40 ? url.substring(0, 40) + '...' : url;
        return '<a href="' + ns.escHtml(url) + '" target="_blank" rel="noopener noreferrer" title="' + ns.escHtml(url) + '">' +
            ns.escHtml(display) + '</a>';
    };

    ns.renderValid = function (value) {
        if (ys.isNullOrEmpty(value)) {
            return '';
        }
        if (String(value) === '-1') {
            return '<span class="badge badge-default" style="background-color:#95a5a6;">未检测</span>';
        }
        return String(value) === '1'
            ? '<span class="badge badge-primary" style="background-color:#27ae60;">有效</span>'
            : '<span class="badge badge-default" style="background-color:#95a5a6;">无效</span>';
    };

    /*
     * 地址与分页模板的前端预检。
     * 这里只做「前缀 + 占位符」这类能立刻判断的检查，最终结论以服务端返回的提示为准，
     * 服务端规则见 SiteCollectUrlRules / SiteCollectPageTemplate。
     */
    ns.checkHttpUrl = function (url) {
        return /^https?:\/\/\S+$/i.test($.trim(url || ''));
    };

    // 返回空字符串表示通过，否则为提示文案
    ns.checkPageTemplate = function (template) {
        var value = $.trim(template || '');
        if (value === '') {
            return '分页链接为空，无法进行分页列表采集';
        }
        if (!/\{page\}/i.test(value)) {
            return '分页链接需包含 {page} 占位符，如 https://example.com/page/{page}';
        }
        if (!/^https?:\/\//i.test(value.replace(/\{page\}/gi, '1'))) {
            return '分页链接需以 http:// 或 https:// 开头';
        }
        return '';
    };

    // 进度悬浮面板联动：任一子进度条可见时才显示面板，全部隐藏时隐藏面板，
    // 避免页面加载完成后看到一个空白的悬浮面板。
    // 注意不能用 :visible 判断：它要求祖先也可见，父面板 display:none 时会误判
    ns.syncProgressPanel = function () {
        var anyVisible = false;
        $('#collectProgressBar,#transferProgressBar,#autoImageProgressBar,#infoAnalyzeProgressBar').each(function () {
            if ($(this).css('display') !== 'none') {
                anyVisible = true;
                return false;
            }
        });
        if (anyVisible) {
            $('#collectProgressPanel').show();
        } else {
            $('#collectProgressPanel').hide();
        }
    };

    // 页面入口：DOM 就绪后由视图调用一次
    ns.init = function () {
        ns.syncProgressPanel();
        ns.grid.init();
        ns.sites.initMode();
        ns.sites.load();
        ns.collect.resume();
        $(window).on('resize', function () {
            $('#gridTable').bootstrapTable('resetView');
        });
    };
})(window, jQuery);
