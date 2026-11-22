/*
 * 通用图片查看器（灯箱）
 * 用法：ys.imageViewer.open({ images: ['https://a.jpg', 'https://b.jpg'], index: 0 });
 *       ys.imageViewer.close();
 * 说明：单例 DOM，首次打开时创建，之后复用同一节点，只替换图片地址与计数；
 *       图片按原始比例完整显示（contain），多图支持左右切换、指示点与计数，单图隐藏切换控件。
 */
;(function ($, window) {
    'use strict';

    var ys = window.ys = window.ys || {};
    var viewer = ys.imageViewer = ys.imageViewer || {};

    // 压在 layer 弹窗（约 19891014）之上，避免从弹窗内部的图片打开时被遮挡
    var VIEWER_Z_INDEX = 20000000;
    var EVENT_NS = '.ysImageViewer';
    // 指示点最多展示个数，超出只保留计数，避免图片过多时底栏被撑开
    var MAX_DOTS = 7;

    var $root = null;
    var $img, $title, $foot, $count, $dots, $prev, $next;
    var opened = false;
    var images = [];
    var index = 0;
    var bodyOverflow = '';

    // 地址归一化：去空白、去空项，兼容传入单个字符串
    function normalizeImages(value) {
        var list = [];
        if (value === null || value === undefined) {
            return list;
        }
        if (!$.isArray(value)) {
            value = [value];
        }
        $.each(value, function (i, item) {
            var url = $.trim(item === null || item === undefined ? '' : String(item));
            if (url !== '') {
                list.push(url);
            }
        });
        return list;
    }

    // 单例 DOM：只创建一次，后续开关复用，减少节点反复插拔
    function build() {
        if ($root) {
            return;
        }
        var html = '<div class="ys-imgv" role="dialog" aria-modal="true" aria-label="查看图片">' +
            '<div class="ys-imgv-mask"></div>' +
            '<div class="ys-imgv-panel">' +
            '<div class="ys-imgv-bar">' +
            '<span class="ys-imgv-title"></span>' +
            '<a class="ys-imgv-close" href="javascript:;" title="关闭"><i class="fa fa-close"></i></a>' +
            '</div>' +
            '<div class="ys-imgv-stage">' +
            '<a class="ys-imgv-arrow is-prev" href="javascript:;" title="上一张"><i class="fa fa-angle-left"></i></a>' +
            '<img class="ys-imgv-img" alt="" />' +
            '<span class="ys-imgv-tip ys-imgv-loading"><i class="fa fa-spinner fa-spin"></i> 加载中…</span>' +
            '<span class="ys-imgv-tip ys-imgv-error"><i class="fa fa-picture-o"></i> 图片加载失败</span>' +
            '<a class="ys-imgv-arrow is-next" href="javascript:;" title="下一张"><i class="fa fa-angle-right"></i></a>' +
            '</div>' +
            '<div class="ys-imgv-foot">' +
            '<span class="ys-imgv-count"></span>' +
            '<span class="ys-imgv-dots"></span>' +
            '</div>' +
            '</div>' +
            '</div>';

        $root = $(html).css('z-index', VIEWER_Z_INDEX).appendTo('body');
        $img = $root.find('.ys-imgv-img');
        $title = $root.find('.ys-imgv-title');
        $foot = $root.find('.ys-imgv-foot');
        $count = $root.find('.ys-imgv-count');
        $dots = $root.find('.ys-imgv-dots');
        $prev = $root.find('.ys-imgv-arrow.is-prev');
        $next = $root.find('.ys-imgv-arrow.is-next');

        // 事件统一带命名空间，便于日后销毁时一次性解绑
        $root.on('click' + EVENT_NS, '.ys-imgv-mask, .ys-imgv-close', function (e) {
            e.preventDefault();
            viewer.close();
        });
        $root.on('click' + EVENT_NS, '.ys-imgv-arrow.is-prev', function (e) {
            e.preventDefault();
            e.stopPropagation();
            go(index - 1);
        });
        $root.on('click' + EVENT_NS, '.ys-imgv-arrow.is-next', function (e) {
            e.preventDefault();
            e.stopPropagation();
            go(index + 1);
        });
        $root.on('click' + EVENT_NS, '.ys-imgv-dot', function (e) {
            e.preventDefault();
            e.stopPropagation();
            go(parseInt($(this).attr('data-index'), 10) || 0);
        });
        // 图片加载完成 / 失败：只切换状态类，不重建节点
        $img.on('load' + EVENT_NS, function () {
            setStage('loaded');
        }).on('error' + EVENT_NS, function () {
            setStage('error');
        });
        // 面板铺满全屏，遮罩多数情况下点不到，因此舞台空白处（图片与箭头之外）也算空白区
        $root.on('click' + EVENT_NS, '.ys-imgv-stage', function (e) {
            if (e.target === this) {
                viewer.close();
            }
        });
        // 点击图片本身不关闭
        $root.on('click' + EVENT_NS, '.ys-imgv-img, .ys-imgv-bar, .ys-imgv-foot', function (e) {
            e.stopPropagation();
        });
    }

    // 舞台状态：loading（转圈）/ loaded（显示图片）/ error（失败占位）
    function setStage(name) {
        $root.removeClass('ys-imgv-state-loading ys-imgv-state-loaded ys-imgv-state-error')
            .addClass('ys-imgv-state-' + name);
    }

    function renderDots(total) {
        $dots.empty();
        if (total < 2 || total > MAX_DOTS) {
            return;
        }
        var html = '';
        for (var i = 0; i < total; i++) {
            html += '<a class="ys-imgv-dot" href="javascript:;" data-index="' + i + '" title="第 ' + (i + 1) + ' 张"></a>';
        }
        $dots.html(html);
    }

    // 预加载左右相邻图片，切换时减少白屏等待
    function preload(current) {
        var total = images.length;
        if (total < 2) {
            return;
        }
        var targets = [images[(current + 1) % total], images[(current - 1 + total) % total]];
        $.each(targets, function (i, url) {
            var img = new Image();
            img.onerror = function () {
                img = null;
            };
            img.src = url;
        });
    }

    function go(target) {
        var total = images.length;
        if (total === 0) {
            return;
        }
        // 取模归一化，实现首尾循环切换
        index = ((target % total) + total) % total;
        var url = images[index];

        setStage('loading');
        $img.removeClass('is-ready').attr('src', url);
        // 命中缓存时 load 事件不会再触发，直接标记为已加载
        if ($img[0].complete && $img[0].naturalWidth > 0) {
            setStage('loaded');
        }
        $count.text((index + 1) + ' / ' + total);
        $dots.children().removeClass('is-active').eq(index).addClass('is-active');
        preload(index);
    }

    /*
     * 打开查看器
     * @param {Object} options
     *        images {Array|String} 图片地址，必填
     *        index  {Number}       起始下标，默认 0
     *        title  {String}       顶部标题，可选，不传则不显示标题
     */
    viewer.open = function (options) {
        options = options || {};
        var list = normalizeImages(options.images);
        if (list.length === 0) {
            if (typeof ys.msgWarning === 'function') {
                ys.msgWarning('没有可查看的图片');
            }
            return;
        }

        build();
        images = list;

        var start = parseInt(options.index, 10);
        if (isNaN(start) || start < 0 || start >= images.length) {
            start = 0;
        }

        // 标题可选：不传（或传空）即不显示标题，不留默认文案
        $title.text($.trim(options.title === null || options.title === undefined ? '' : String(options.title)));

        renderDots(images.length);
        // 单图不展示切换控件与底栏，只保留关闭按钮
        var multi = images.length > 1;
        $prev.toggleClass('is-hidden', !multi);
        $next.toggleClass('is-hidden', !multi);
        $foot.toggleClass('is-hidden', !multi);

        if (!opened) {
            opened = true;
            // 记录并锁定页面滚动，关闭时原样恢复，避免多次开关导致滚动失效
            bodyOverflow = $('body').css('overflow') || '';
            $('body').css('overflow', 'hidden');
            $root.addClass('is-open');
        }
        go(start);
    };

    // 关闭查看器：幂等，重复调用无副作用
    viewer.close = function () {
        if (!$root || !opened) {
            return;
        }
        opened = false;
        $root.removeClass('is-open ys-imgv-state-loading ys-imgv-state-loaded ys-imgv-state-error');
        $('body').css('overflow', bodyOverflow);
        // 关闭后释放图片地址，避免继续占用带宽与内存
        $img.removeAttr('src').removeClass('is-ready');
        images = [];
        index = 0;
    };

    // 是否处于打开状态，供调用方判断（例如避免重复打开）
    viewer.isOpen = function () {
        return opened;
    };
})(window.jQuery, window);
