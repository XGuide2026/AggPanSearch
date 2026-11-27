/*
 * 卡片列表公共脚本（跨页面复用）
 * 使用方：PanManage/PanSourceCollect（采集资源）、PanManage/PanSource（本地资源）
 * 职责：卡片 HTML 渲染片段 + 卡片列表交互（勾选描边、全选本页、封面看大图、简介超长截断）。
 * 依赖：jQuery、bootstrap-table；封面看大图依赖 aggpansearch/js/aggpansearch-image-viewer.js（未引入时自动降级为不可点击）。
 * 样式：aggpansearch/css/aggpansearch-card-list.css（collect-card-* 为跨页公共类）。
 */
(function (window, $) {
    'use strict';

    var ys = window.ys = window.ys || {};
    var cardList = ys.cardList = ys.cardList || {};

    // 卡片内容可能来自外部站点或用户录入，统一转义后再拼接，避免 XSS
    function escHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    // 本地上传的图片存的是相对路径（如 /upload/xxx.jpg），展示与查看大图都需要补上站点根路径
    function resolveUrl(url) {
        if (/^(https?:)?\/\//i.test(url) || url.indexOf('data:') === 0) {
            return url;
        }
        var root = String(window.ctx || '/').replace(/\/+$/, '');
        return root + (url.charAt(0) === '/' ? url : '/' + url);
    }

    // 图片字段是多张以英文分号 ";" 分隔存放的，这里统一拆成 trim 后的地址数组
    // 空白项不计入，封面、张数角标、图片查看器都复用这一份解析结果
    function parseImages(value) {
        var list = [];
        if (value === undefined || value === null || $.trim(String(value)) === '') {
            return list;
        }
        $.each(String(value).split(';'), function (index, item) {
            var url = $.trim(item);
            if (url !== '') {
                list.push(resolveUrl(url));
            }
        });
        return list;
    }

    // 封面：首图 + 右上角张数；无图或加载失败时展示渐变占位块
    // 有图时整块封面可点击查看大图，全部地址以 ";" 拼进 data-images，供查看器轮播
    function renderCover(images, options) {
        var opts = $.extend({ placeholder: '无封面', failed: '图片加载失败' }, options);
        var cover = images.length > 0 ? images[0] : '';
        var imageTotal = images.length;
        var clickable = cover !== '';
        var html = '<div class="collect-card-cover' + (clickable ? ' is-clickable' : ' is-empty') + '"';
        if (clickable) {
            html += ' data-images="' + escHtml(images.join(';')) + '"' +
                ' title="共 ' + imageTotal + ' 张图片"';
        }
        html += '>';
        if (clickable) {
            // 加载失败说明首图不可用，同步摘掉可点击标记，避免点到空图
            html += '<img src="' + escHtml(cover) + '" alt="" loading="lazy" ' +
                'onerror="this.style.display=\'none\';this.parentNode.classList.remove(\'is-clickable\');' +
                'this.parentNode.classList.add(\'is-empty\');" />';
        }
        // 有图时在封面右上角给出张数，与左上角勾选框分列两侧
        if (imageTotal > 0) {
            html += '<span class="collect-card-cover-count" title="共 ' + imageTotal + ' 张图片">' +
                '<i class="fa fa-picture-o"></i> ' + imageTotal + '</span>';
        }
        // 无图 / 加载失败：暗淡背景 + 图片裂开图标（样式见 aggpansearch-card-list.css），文本仅作辅助说明
        html += '<span class="collect-card-cover-ph"><i class="fa fa-file-image-o"></i>' +
            (cover === '' ? opts.placeholder : opts.failed) + '</span>';
        return html + '</div>';
    }

    // 标签行：tags 为 [{ text, type, title }]，type 取 primary / ok / warn / danger / mute
    function renderTags(tags) {
        var html = '';
        $.each(tags || [], function (index, item) {
            if (!item || $.trim(String(item.text || '')) === '') {
                return;
            }
            var cls = 'collect-card-tag' + (item.type ? ' is-' + item.type : '');
            var title = item.title ? ' title="' + escHtml(item.title) + '"' : '';
            html += '<span class="' + cls + '"' + title + '>' + escHtml(item.text) + '</span>';
        });
        return html === '' ? '' : '<div class="collect-card-tags">' + html + '</div>';
    }

    // 标题固定两行，超出省略；空标题用占位文案，避免卡片出现空白标题区
    // options.saved 为 true 时在标题后追加绿色高亮「已转存」徽标（本地资源页不传，行为不变）
    function renderTitle(text, placeholder, options) {
        var value = $.trim(text == null ? '' : String(text));
        var display = value === '' ? (placeholder || '（未命名）') : value;
        var badge = (options && options.saved) ? '<span class="collect-card-title-saved">已转存</span>' : '';
        return '<div class="collect-card-title" title="' + escHtml(value) + '">' + escHtml(display) + badge + '</div>';
    }

    // 副标题：单行省略，完整内容放 title 属性
    function renderSub(text) {
        var value = $.trim(text == null ? '' : String(text));
        if (value === '') {
            return '';
        }
        return '<div class="collect-card-sub" title="' + escHtml(value) + '">' + escHtml(value) + '</div>';
    }

    // 简介：紧跟标签行，行数随内容自适应（瀑布流的高度差异之一），
    // 超过 CSS 上限（默认 12 行）时由 markIntroOverflow 在末行末尾补「查看完整」
    // options：{ id, fn, title }，传了 fn + id 时正文包一层 a，点击调 fn(id)（如打开资源介绍编辑弹窗）
    function renderIntro(text, emptyText, options) {
        var opts = options || {};
        var value = $.trim(text == null ? '' : String(text));
        var display = escHtml(value === '' ? (emptyText || '暂无简介') : value);
        var safeId = escHtml(opts.id == null ? '' : String(opts.id));
        var clickable = !!opts.fn && safeId !== '';
        var html = '<div class="collect-card-intro' + (value === '' ? ' is-empty' : '') +
            (clickable ? ' is-editable' : '') + '">';
        if (clickable) {
            html += '<div class="collect-card-intro-text">' +
                '<a class="collect-card-intro-link" href="javascript:;" title="' + escHtml(opts.title || '点击编辑资源介绍') + '"' +
                ' onclick="event.stopPropagation();' + opts.fn + '(\'' + safeId + '\');return false;">' +
                display + '</a></div>';
        }
        else {
            html += '<div class="collect-card-intro-text">' + display + '</div>';
        }
        html += '</div>';
        return html;
    }

    // 元信息行：items 为 [{ icon, text, title }]，左右两端对齐，超长省略
    function renderMeta(items) {
        var html = '';
        $.each(items || [], function (index, item) {
            if (!item) {
                return;
            }
            var text = item.text == null ? '' : String(item.text);
            var title = item.title ? item.title : text;
            html += '<span title="' + escHtml(title) + '">' +
                (item.icon ? '<i class="fa ' + item.icon + '"></i> ' : '') + escHtml(text) + '</span>';
        });
        return html === '' ? '' : '<div class="collect-card-meta">' + html + '</div>';
    }

    // 资源链接：地址过长时截断显示，完整地址放 title，点击新窗口打开
    function renderLink(url, emptyText) {
        var value = $.trim(url == null ? '' : String(url));
        if (value === '') {
            return '<div class="collect-card-link"><span style="color:#bbb;">' + escHtml(emptyText || '无资源链接') + '</span></div>';
        }
        var display = value.length > 40 ? value.substring(0, 40) + '...' : value;
        return '<div class="collect-card-link" title="' + escHtml(value) + '">' +
            '<a href="' + escHtml(value) + '" target="_blank" rel="noopener noreferrer">' + escHtml(display) + '</a>' +
            '</div>';
    }

    // 底部操作栏：修改 / 删除直接作用于当前卡片，无需先勾选
    // editFnName / delFnName 为页面提供的全局函数名，调用时传入当前行 Id
    function renderFoot(id, editFnName, delFnName) {
        var safeId = escHtml(id == null ? '' : id);
        return '<div class="collect-card-foot">' +
            '<a class="collect-btn collect-btn-edit" href="javascript:;" onclick="' + editFnName + '(\'' + safeId + '\')">' +
            '<i class="fa fa-edit"></i> 修改</a>' +
            '<a class="collect-btn collect-btn-del" href="javascript:;" onclick="' + delFnName + '(\'' + safeId + '\')">' +
            '<i class="fa fa-remove"></i> 删除</a>' +
            '</div>';
    }

    // 简介行数上限兜底值：正常情况下直接读 CSS 的 max-height，这里只在读不到时使用
    var INTRO_MAX_LINES = 12;

    // 简介高度上限：以样式表的 max-height 为唯一来源（12 行 × 16px = 192px），
    // 取不到具体像素值（none / auto）时按 12 行 × 行高兜底
    function introMaxHeight(box, lineHeight) {
        var maxHeight = parseFloat(window.getComputedStyle(box).maxHeight);
        if (!maxHeight || isNaN(maxHeight)) {
            maxHeight = lineHeight * INTRO_MAX_LINES;
        }
        return maxHeight;
    }

    // 简介超过上限行数时截断正文，并在末行末尾接「查看完整」，点击弹窗显示全文
    // 「查看完整」是内联元素，与正文同处一个文字流，靠二分截断保证两者一起落在上限行数内、互不重叠
    function markIntroOverflow(table) {
        var $scope = typeof table === 'string' ? $(table) : $(table || '#gridTable');
        $scope.find('.collect-card-intro:not(.is-empty)').each(function () {
            var $box = $(this);
            var $text = $box.children('.collect-card-intro-text');
            if ($text.length === 0) {
                return;
            }
            // 简介正文可能被 a 包着（点击可编辑），截断时只改 a 内文字，保留链接本身
            var $target = $text.children('a.collect-card-intro-link').first();
            if ($target.length === 0) {
                $target = $text;
            }
            // 首次渲染时缓存全文，之后每次重算都从全文开始，卡片宽度变化可重新截断
            var full = $text.data('introFull');
            if (full === undefined) {
                full = $.trim($text.text());
                $text.data('introFull', full);
            }
            $target.text(full);
            $box.children('.collect-card-intro-more').remove();

            var box = $box[0];
            var lineHeight = parseFloat(window.getComputedStyle(box).lineHeight);
            if (!lineHeight || isNaN(lineHeight)) {
                lineHeight = 16;
            }
            var maxHeight = introMaxHeight(box, lineHeight);
            if (box.scrollHeight <= maxHeight + 1) {
                return;
            }

            $('<a class="collect-card-intro-more" href="javascript:;">…查看完整</a>')
                .on('click', function (e) {
                    // 简介按钮不参与卡片勾选 / 详情，只弹窗
                    e.preventDefault();
                    e.stopPropagation();
                    showIntro(full);
                })
                .appendTo($box);

            // 二分找出「正文 + 查看完整」仍能放进 4 行的最长正文
            var low = 0;
            var high = full.length;
            var fit = 0;
            while (low <= high) {
                var mid = Math.floor((low + high) / 2);
                $target.text(full.substring(0, mid));
                if (box.scrollHeight <= maxHeight + 1) {
                    fit = mid;
                    low = mid + 1;
                } else {
                    high = mid - 1;
                }
            }
            $target.text(full.substring(0, fit).replace(/\s+$/, ''));
        });
    }

    // 简介全文弹窗：正文保留换行，超出高度时弹窗内部滚动
    function showIntro(text) {
        ys.openDialogContent({
            title: '资源简介',
            content: '<div class="collect-intro-dialog">' + escHtml(text) + '</div>',
            width: '640px',
            height: 'auto',
            shadeClose: true,
            closeBtn: 1,
            btn: null
        });
    }

    // 选项归一化：表格与「全选本页」选择器都给了默认值，页面只传差异项
    function normalizeOptions(options) {
        return $.extend({ table: '#gridTable', selectAll: '#chkSelectAllPage', onSelection: null }, options || {});
    }

    // 勾选状态同步：卡片描边 + 「全选本页」勾选态，返回当前勾选数量
    function syncSelection(options) {
        var opts = normalizeOptions(options);
        var $table = $(opts.table);
        var $rows = $table.find('> tbody > tr:not(.no-records-found)');
        var count = 0;
        $rows.each(function () {
            var $tr = $(this);
            var checked = $tr.find('input[type="checkbox"]').first().prop('checked');
            if (checked) {
                count++;
            }
            // bootstrap-table 的 selected 类只作用于表格行样式，卡片用自己的类名描边
            $tr.toggleClass('is-checked', !!checked);
        });

        // 「全选本页」跟随当前页勾选情况：本页全部勾选时置为已选，取消任意一条则回退
        var $selectAll = $(opts.selectAll);
        if ($selectAll.length > 0) {
            $selectAll.prop('checked', $rows.length > 0 && count >= $rows.length);
        }
        return count;
    }

    // 绑定卡片列表交互：events 用 .cardList 命名空间，重复初始化时先解绑旧处理器
    cardList.bind = function (options) {
        var opts = normalizeOptions(options);
        var $table = $(opts.table);
        var $selectAll = $(opts.selectAll);

        function refresh() {
            var count = syncSelection(opts);
            if ($.isFunction(opts.onSelection)) {
                opts.onSelection(count);
            }
        }

        // 勾选变化与数据加载完成都要刷新按钮状态与卡片高亮
        $table.off('.cardList');
        $table.on('check.bs.table.cardList uncheck.bs.table.cardList check-all.bs.table.cardList ' +
            'uncheck-all.bs.table.cardList load-success.bs.table.cardList', refresh);

        // 每次行渲染完成后探测简介是否超过高度上限（CSS max-height，约 12 行）
        $table.on('post-body.bs.table.cardList', function () {
            markIntroOverflow(opts.table);
        });

        // 封面点击查看大图：卡片随分页 / 排序重渲染，用委托绑定一次即可；
        // 阻止冒泡，避免点击封面连带触发行勾选等表格行为
        $table.on('click.cardList', '.collect-card-cover.is-clickable', function (e) {
            e.preventDefault();
            e.stopPropagation();
            // data-images 是 ";" 分隔的地址，读取时浏览器已自动还原转义字符
            var value = $.trim($(this).attr('data-images') || '');
            if (value === '' || !ys.imageViewer || typeof ys.imageViewer.open !== 'function') {
                return;
            }
            ys.imageViewer.open({
                images: value.split(';'),
                index: 0
            });
        });

        // 卡片没有表头，用工具栏上的复选框代替表头全选，作用范围是当前页
        $selectAll.off('change.cardList').on('change.cardList', function () {
            if ($(this).prop('checked')) {
                $table.bootstrapTable('checkAll');
            } else {
                $table.bootstrapTable('uncheckAll');
            }
            refresh();
        });

        // 窗口宽度变化会改变卡片宽度与行数，简介需要重算；做防抖避免连续重排
        var resizeTimer = null;
        $(window).off('resize.cardList').on('resize.cardList', function () {
            if (resizeTimer) {
                clearTimeout(resizeTimer);
            }
            resizeTimer = setTimeout(function () {
                markIntroOverflow(opts.table);
            }, 120);
        });

        refresh();
        return { refresh: refresh, syncSelection: syncSelection };
    };

    cardList.escHtml = escHtml;
    cardList.resolveUrl = resolveUrl;
    cardList.parseImages = parseImages;
    cardList.renderCover = renderCover;
    cardList.renderTags = renderTags;
    cardList.renderTitle = renderTitle;
    cardList.renderSub = renderSub;
    cardList.renderIntro = renderIntro;
    cardList.renderMeta = renderMeta;
    cardList.renderLink = renderLink;
    cardList.renderFoot = renderFoot;
    cardList.markIntroOverflow = markIntroOverflow;
    cardList.showIntro = showIntro;
    cardList.syncSelection = syncSelection;
})(window, jQuery);
