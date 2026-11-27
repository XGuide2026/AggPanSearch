/*
 * 站点采集 - 配置分类弹窗页
 * 职责：自动抓取站点分类、手工增改删分类项、把站点分类绑定到「分类资源」、保存映射。
 * 说明：映射随站点一起保存到 collect-sites.json；采集入库时按这里的绑定关系把源站分类换算成
 *       「分类资源」Id（见 PanSourceCollectCategoryMap），未绑定或名称对不上的记录写为未分类。
 */
(function (window, $) {
    'use strict';

    var ns = window.PanSourceCollect = window.PanSourceCollect || {};
    var page = ns.siteCategory = ns.siteCategory || {};
    var api = window.PanSourceSiteCategoryApi || {};

    var state = {
        siteId: '',
        site: null,
        options: [],
        rows: [],
        fetching: false,
        saving: false
    };

    function esc(value) {
        return ns.escHtml ? ns.escHtml(value) : String(value == null ? '' : value);
    }

    // ---------- 提示条 ----------

    function showTip(message, type) {
        var $tip = $('#categoryTip');
        if (!message) {
            $tip.hide().text('');
            return;
        }
        $tip.removeClass('tip-info tip-success tip-error')
            .addClass('tip-' + (type || 'info'))
            .text(message)
            .show();
    }

    // ---------- 渲染 ----------

    function renderSourceSelect(selectedId) {
        var html = '<option value="0">未绑定</option>';
        var matched = false;
        $.each(state.options, function (i, option) {
            var isSelected = option.Id === selectedId;
            if (isSelected) {
                matched = true;
            }
            html += '<option value="' + option.Id + '"' + (isSelected ? ' selected' : '') + '>' +
                esc(option.Name) + (option.Status === 0 ? '（不显示）' : '') + '</option>';
        });
        // 绑定的分类资源已被删除时保留原值，避免静默丢失绑定
        if (selectedId > 0 && !matched) {
            html += '<option value="' + selectedId + '" selected>分类资源已删除（Id ' + selectedId + '）</option>';
        }
        return html;
    }

    // 已有分类名只读展示（视觉等同纯文本）；「手工添加」的新行或名称为空的行仍可输入
    function renderNameCell(name, editable) {
        if (editable) {
            return '<input type="text" class="form-control input-sm cat-name" maxlength="50" placeholder="分类名称" value="' + esc(name) + '" />';
        }
        return '<input type="text" class="form-control input-sm cat-name cat-name-readonly" readonly tabindex="-1"' +
            ' title="' + esc(name) + '" value="' + esc(name) + '" />';
    }

    function render() {
        var html = '';
        $.each(state.rows, function (i, row) {
            var name = $.trim(row.Name || '');
            var url = $.trim(row.Url || '');
            var editable = row.IsNew === true || name === '';
            var urlHtml = url === ''
                ? '<span class="category-url-empty">手工录入</span>'
                : '<a class="category-url" href="' + esc(url) + '" target="_blank" rel="noopener" title="' + esc(url) + '">' + esc(url) + '</a>';
            html += '<tr data-i="' + i + '" data-url="' + esc(url) + '" data-new="' + (editable ? 1 : 0) + '">' +
                '<td>' + renderNameCell(name, editable) + '</td>' +
                '<td class="category-source-cell"><select class="form-control input-sm cat-source">' + renderSourceSelect(row.SourceCategoryId || 0) + '</select></td>' +
                '<td class="category-url-cell">' + urlHtml + '</td>' +
                '<td class="text-center"><a class="category-remove" onclick="PanSourceSiteCategory.removeRow(' + i + ')">删除</a></td>' +
                '</tr>';
        });
        $('#categoryTableBody').html(html);
        $('#categoryEmpty').toggle(state.rows.length === 0);
        initSourceSelects();
        updateStats();
    }

    // 分类资源下拉统一用项目通用下拉（select2），宽度占列宽一半，关闭搜索框
    function initSourceSelects() {
        $('#categoryTableBody .cat-source').each(function () {
            var $el = $(this);
            if ($el.data('select2')) {
                return;
            }
            $el.select2({
                minimumResultsForSearch: Infinity,
                width: '50%',
                dropdownAutoWidth: true
            });
        });
    }

    // 统计：共 N 项，已绑定 M 项（以页面当前输入为准）
    function updateStats() {
        var total = 0;
        var bound = 0;
        $('#categoryTableBody tr[data-i]').each(function () {
            var $tr = $(this);
            if ($.trim($tr.find('.cat-name').val() || '') === '' && $.trim($tr.attr('data-url') || '') === '') {
                return;
            }
            total++;
            if ((parseInt($tr.find('.cat-source').val(), 10) || 0) > 0) {
                bound++;
            }
        });
        $('#categoryStats').text('共 ' + total + ' 项，已绑定 ' + bound + ' 项');
    }

    // 把页面上的输入同步回 state.rows，避免增删行时丢失用户已输入的内容
    function syncFromDom() {
        var rows = [];
        $('#categoryTableBody tr[data-i]').each(function () {
            var $tr = $(this);
            rows.push({
                Name: $.trim($tr.find('.cat-name').val() || ''),
                Url: $.trim($tr.attr('data-url') || ''),
                SourceCategoryId: parseInt($tr.find('.cat-source').val(), 10) || 0,
                IsNew: $tr.attr('data-new') === '1'
            });
        });
        state.rows = rows;
    }

    // ---------- 数据加载 ----------

    function applySite(site) {
        state.site = site;
        $('#categorySiteName').text(site.SiteName || '-');
        var url = $.trim(site.SiteUrl || '');
        var $link = $('#categorySiteUrl');
        if (url === '') {
            $link.text('未配置站点链接').attr('href', 'javascript:void(0)').addClass('empty');
        } else {
            $link.text(url).attr('href', url).removeClass('empty');
        }

        state.rows = [];
        $.each(site.Categories || [], function (i, item) {
            state.rows.push({
                Name: $.trim(item.Name || ''),
                Url: $.trim(item.Url || ''),
                SourceCategoryId: item.SourceCategoryId || 0,
                IsNew: false
            });
        });
        render();
    }

    function loadSite() {
        // 优先取父页面已加载的站点数据，避免多一次请求
        var site = null;
        try {
            site = parent.PanSourceCollect && parent.PanSourceCollect.sites
                ? parent.PanSourceCollect.sites.find(state.siteId)
                : null;
        } catch (e) {
            site = null;
        }
        if (site) {
            applySite(site);
            return;
        }

        $.ajax({
            url: api.siteList,
            type: 'get',
            dataType: 'json',
            success: function (result) {
                var list = (result.Tag === 1 && result.Data) ? result.Data : [];
                var matched = null;
                $.each(list, function (i, item) {
                    if (item.Id === state.siteId) {
                        matched = item;
                        return false;
                    }
                });
                if (matched) {
                    applySite(matched);
                } else {
                    showTip('站点不存在或已被删除，请关闭弹窗后刷新页面', 'error');
                }
            },
            error: function () {
                showTip('站点信息加载失败，请关闭弹窗后重试', 'error');
            }
        });
    }

    function loadOptions() {
        $.ajax({
            url: api.categoryOptions,
            type: 'get',
            dataType: 'json',
            success: function (result) {
                var list = (result.Tag === 1 && result.Data) ? result.Data : [];
                state.options = $.map(list, function (item) {
                    return {
                        Id: item.Id || item.id || 0,
                        Name: $.trim(item.Name || item.name || ''),
                        Status: item.Status === undefined ? 1 : item.Status
                    };
                });
                render();
            },
            error: function () {
                showTip('分类资源加载失败，「我的网盘分类」下拉暂不可用', 'error');
            }
        });
    }

    // ---------- 交互 ----------

    page.init = function () {
        if ($('#siteCategoryPage').length === 0) {
            return;
        }
        state.siteId = ys.request('siteId') || '';
        if (state.siteId === '') {
            showTip('缺少站点参数，请关闭弹窗后重试', 'error');
            return;
        }

        // 输入变化时刷新统计
        $('#categoryTableBody').on('input change', '.cat-name, .cat-source', updateStats);

        loadSite();
        loadOptions();
    };

    page.addRow = function () {
        syncFromDom();
        state.rows.push({ Name: '', Url: '', SourceCategoryId: 0, IsNew: true });
        render();
        $('#categoryTableBody tr[data-i]:last .cat-name').trigger('focus');
    };

    page.removeRow = function (index) {
        syncFromDom();
        if (index < 0 || index >= state.rows.length) {
            return;
        }
        state.rows.splice(index, 1);
        render();
    };

    page.fetchCategories = function () {
        if (state.fetching) {
            return;
        }
        syncFromDom();
        state.fetching = true;
        var $btn = $('#btnFetchCategories').addClass('disabled');
        $btn.find('i').removeClass('fa-cloud-download').addClass('fa-spinner fa-spin');
        showTip('正在抓取站点分类，请稍候…', 'info');

        $.ajax({
            url: api.fetchSiteCategories,
            type: 'get',
            data: { siteId: state.siteId },
            dataType: 'json',
            success: function (result) {
                if (result.Tag !== 1) {
                    showTip(result.Message || '抓取站点分类失败，可手工添加分类', 'error');
                    return;
                }
                var exist = {};
                $.each(state.rows, function (i, row) {
                    if (row.Url) {
                        exist[row.Url.toLowerCase()] = true;
                    }
                });

                var added = 0;
                $.each(result.Data || [], function (i, item) {
                    var url = $.trim(item.Url || '');
                    if (url === '' || exist[url.toLowerCase()]) {
                        return;
                    }
                    exist[url.toLowerCase()] = true;
                    state.rows.push({ Name: $.trim(item.Name || ''), Url: url, SourceCategoryId: 0, IsNew: false });
                    added++;
                });
                render();

                if (added > 0) {
                    showTip('抓取完成，新增 ' + added + ' 个分类，请为它们绑定分类资源', 'success');
                } else {
                    showTip(result.Message || '没有发现新的分类，可手工添加', 'info');
                }
            },
            error: function (xhr) {
                showTip('抓取失败：' + (xhr.statusText || '网络异常') + '，可手工添加分类', 'error');
            },
            complete: function () {
                state.fetching = false;
                $btn.removeClass('disabled');
                $btn.find('i').removeClass('fa-spinner fa-spin').addClass('fa-cloud-download');
            }
        });
    };

    // 由父页面弹窗的按钮回调调用：校验通过则保存并关闭弹窗
    page.submitForm = function (index) {
        if (state.saving) {
            return false;
        }
        syncFromDom();

        var items = [];
        var error = '';
        var seen = {};
        $.each(state.rows, function (i, row) {
            var name = $.trim(row.Name || '');
            var url = $.trim(row.Url || '');
            var sourceId = row.SourceCategoryId || 0;
            // 完全空白的行不提交
            if (name === '' && url === '' && sourceId === 0) {
                return true;
            }
            if (name === '') {
                error = '第 ' + (i + 1) + ' 行的分类名称不能为空';
                return false;
            }
            if (name.length > 50) {
                error = '分类名称不能超过 50 个字符';
                return false;
            }
            var key = (name + '|' + url).toLowerCase();
            if (seen[key]) {
                error = '存在重复的分类「' + name + '」，请修改后重试';
                return false;
            }
            seen[key] = true;
            items.push({ Name: name, Url: url, SourceCategoryId: sourceId });
        });

        if (error !== '') {
            ys.msgWarning(error);
            return false;
        }

        state.saving = true;
        ys.ajax({
            url: api.saveSiteCategories,
            type: 'post',
            data: {
                siteId: state.siteId,
                categories: JSON.stringify(items)
            },
            success: function (result) {
                state.saving = false;
                if (result.Tag === 1) {
                    if (index !== undefined) {
                        parent.layer.close(index);
                    }
                    parent.layer.msg(result.Message || '保存成功', { icon: 1, time: 1500, shift: 5 });
                    try {
                        parent.PanSourceCollect.sites.load();
                    } catch (e) {
                        // 父页面不可用时忽略，仅关闭弹窗
                    }
                } else {
                    showTip(result.Message || '保存失败', 'error');
                    ys.msgError(result.Message || '保存失败');
                }
            },
            error: function (xhr) {
                state.saving = false;
                showTip('保存失败：' + (xhr.statusText || '网络异常'), 'error');
            }
        });
        return false;
    };

    // 视图内的内联 onclick 依赖该全局对象
    window.PanSourceSiteCategory = page;
})(window, jQuery);
