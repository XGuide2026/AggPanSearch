/*
 * 站点采集 - 左栏站点面板
 * 职责：站点列表加载与卡片渲染、站点新增/编辑弹窗、站点删除、配置分类入口。
 */
(function (window, $) {
    'use strict';

    var ns = window.PanSourceCollect;
    var sites = ns.sites = ns.sites || {};

    // 读取上次使用的采集模式，作为采集弹窗的默认选中项
    sites.initMode = function () {
        var saved = parseInt(sessionStorage.getItem(ns.state.modeKey), 10);
        ns.state.mode = (saved === 0 || saved === 1 || saved === 2) ? saved : 1;
    };

    sites.find = function (id) {
        var list = ns.state.sites;
        for (var i = 0; i < list.length; i++) {
            if (list[i].Id === id) {
                return list[i];
            }
        }
        return null;
    };

    sites.load = function () {
        $.ajax({
            url: ns.api.siteList,
            type: 'get',
            dataType: 'json',
            success: function (result) {
                ns.state.sites = (result.Tag === 1 && result.Data) ? result.Data : [];
                sites.fillSourceSelect();
                sites.render();
            },
            error: function () {
                ns.state.sites = [];
                sites.fillSourceSelect();
                sites.render();
            }
        });
    };

    // 搜索区「采集来源」下拉框：使用框架 ysComboBox，选项来自站点名称
    sites.fillSourceSelect = function () {
        var combo = $('#collectSource');
        if (combo.length === 0) {
            return;
        }
        // 刷新选项前记录当前选中值，避免选择被重置为「全部」
        var current = $('#collectSource_select').val() || '';
        var names = [];
        $.each(ns.state.sites, function (i, site) {
            var name = $.trim(site.SiteName || '');
            if (name !== '' && $.inArray(name, names) === -1) {
                names.push(name);
            }
        });
        combo.ysComboBox({ data: names, search: false });
        if (current !== '' && $.inArray(current, names) !== -1) {
            combo.ysComboBox('setValue', current);
        }
    };

    // 已配置的分类数量（有名称的分类项才算数）
    function categoryCount(site) {
        var count = 0;
        $.each((site && site.Categories) || [], function (i, item) {
            if ($.trim(item.Name || '') !== '') {
                count++;
            }
        });
        return count;
    }

    // 分类配置状态：采集前必须先配置，未配置时卡片上给出明确提示
    function renderCategoryStatus(site) {
        var count = categoryCount(site);
        if (count > 0) {
            return '<div class="site-card-category ok"><i class="fa fa-check-circle"></i> 已配置 ' + count + ' 个分类</div>';
        }
        return '<div class="site-card-category empty"><i class="fa fa-exclamation-circle"></i> 未配置分类，请先配置</div>';
    }

    // 站点链接：单行展示、点击新窗口打开；旧数据缺失时提示未配置
    function renderSiteUrl(site) {
        if (ys.isNullOrEmpty(site.SiteUrl)) {
            return '<div class="site-card-url empty">未配置站点链接</div>';
        }
        var url = ns.escHtml(site.SiteUrl);
        return '<a class="site-card-url" href="' + url + '" target="_blank" rel="noopener" title="' + url + '">' + url + '</a>';
    }

    sites.render = function () {
        var list = ns.state.sites;
        $('#siteCount').text(list.length);
        if (list.length === 0) {
            $('#siteCardList').empty();
            $('#siteEmpty').show();
            return;
        }
        $('#siteEmpty').hide();

        var html = '';
        $.each(list, function (i, site) {
            var id = ns.escHtml(site.Id);
            // 顺序约束：先「配置分类」再「采集」，未配置分类的站点不允许采集
            var blocked = categoryCount(site) === 0;
            var collectDisabled = ns.state.running || blocked;
            html += '<div class="site-card">' +
                '<div class="site-card-head">' +
                '<div class="site-card-title" title="' + ns.escHtml(site.SiteName) + '">' + ns.escHtml(site.SiteName) + '</div>' +
                '<span class="site-card-links">' +
                '<a class="link" onclick="showSiteForm(\'' + id + '\')">编辑</a>' +
                '<a class="link danger" onclick="deleteSite(\'' + id + '\')">删除</a>' +
                '</span>' +
                '</div>' +
                renderSiteUrl(site) +
                renderCategoryStatus(site) +
                '<div class="site-card-actions">' +
                '<a class="btn btn-default btn-xs btn-configure" onclick="openSiteCategory(\'' + id + '\')">' +
                '<span class="site-step-no">1</span>配置分类</a>' +
                '<span class="site-step-arrow"><i class="fa fa-angle-right"></i></span>' +
                '<a class="btn btn-primary btn-xs btn-collect' + (collectDisabled ? ' disabled' : '') + '"' +
                (blocked ? ' data-blocked="1" title="请先配置分类"' : '') +
                ' onclick="collectSite(\'' + id + '\')">' +
                '<span class="site-step-no">2</span>采集</a>' +
                '</div></div>';
        });
        $('#siteCardList').html(html);
        $('#gridTable').bootstrapTable('resetView');
    };

    // 采集进行中时禁用「采集」按钮，避免重复提交；未配置分类的按钮保持禁用
    sites.toggleCollectButtons = function (disabled) {
        ns.state.running = disabled;
        $('#siteCardList .btn-collect').each(function () {
            var $btn = $(this);
            if ($btn.attr('data-blocked') === '1') {
                return;
            }
            $btn.toggleClass('disabled', disabled);
        });
    };

    function formField(label, inputId, value, placeholder, required) {
        return '<div class="form-group">' +
            '<label class="col-sm-3 control-label">' + (required ? '<span class="text-danger">*</span>' : '') + label + '</label>' +
            '<div class="col-sm-9">' +
            '<input id="' + inputId + '" type="text" class="form-control" maxlength="1000" placeholder="' + placeholder + '" value="' + ns.escHtml(value) + '" />' +
            '</div></div>';
    }

    // 站点表单校验：与后端 PanSourceCollectSiteValidator 同口径（名称与站点链接必填，最终以服务端返回为准）
    function validateSiteForm(data) {
        if (data.SiteName === '') {
            return '请输入站点名称';
        }
        if (data.SiteUrl === '') {
            return '请输入站点链接';
        }
        if (!ns.checkHttpUrl(data.SiteUrl)) {
            return '站点链接需以 http:// 或 https:// 开头';
        }
        return '';
    }

    sites.showForm = function (id) {
        var site = id ? sites.find(id) : null;
        if (id && !site) {
            ys.msgWarning('站点不存在，请刷新后重试');
            return;
        }

        var html = '<div class="wrapper animated fadeInRight" style="padding:18px 20px 0 20px;">' +
            '<div class="form-horizontal">' +
            formField('站点名称', 'siteName', site ? site.SiteName : '', '如 多多资源库', true) +
            formField('站点链接', 'siteUrl', site ? site.SiteUrl : '', '如 https://www.duoduoziyuanku.com/', true) +
            '</div>' +
            '<p class="help-block" style="margin-left:15px;">采集前请先点击卡片上的「配置分类」绑定分类资源；列表页地址、分页模板与详情页地址在点击「采集」后按模式手动填写。</p>' +
            '</div>';

        ys.openDialog({
            type: 1,
            title: site ? '编辑站点' : '添加站点',
            content: html,
            width: '640px',
            height: '360px',
            btn: ['保存', '关闭'],
            callback: function (index) {
                var data = {
                    Id: site ? site.Id : '',
                    SiteName: $.trim($('#siteName').val()),
                    SiteUrl: $.trim($('#siteUrl').val())
                };

                var error = validateSiteForm(data);
                if (error !== '') {
                    ys.msgWarning(error);
                    return false;
                }

                ys.ajax({
                    url: ns.api.saveSite,
                    type: 'post',
                    data: data,
                    success: function (result) {
                        if (result.Tag === 1) {
                            layer.close(index);
                            layer.msg(result.Message || '保存成功', { icon: 1, time: 1500, shift: 5 });
                            sites.load();
                        } else {
                            ys.msgError(result.Message || '保存失败');
                        }
                    },
                    error: function (xhr) {
                        ys.msgError('保存失败：' + (xhr.statusText || '网络异常'));
                    }
                });
                return false;
            }
        });
    };

    // 配置分类：独立弹窗页内维护站点分类与「分类资源」的对应关系
    sites.openCategoryConfig = function (id) {
        var site = sites.find(id);
        if (!site) {
            ys.msgWarning('站点不存在，请刷新后重试');
            return;
        }

        ys.openDialog({
            title: '配置分类 - ' + (site.SiteName || ''),
            content: ns.api.categoryForm + '?siteId=' + encodeURIComponent(id),
            width: '920px',
            height: '620px',
            btn: ['保存', '关闭'],
            callback: function (index, layero) {
                // 校验与提交都在配置页面内完成，这里只负责把结果透传给 layer
                var iframeWin = window[layero.find('iframe')[0]['name']];
                if (!iframeWin || !iframeWin.PanSourceSiteCategory) {
                    ys.msgWarning('配置页面尚未加载完成，请稍后重试');
                    return false;
                }
                return iframeWin.PanSourceSiteCategory.submitForm(index);
            }
        });
    };

    sites.remove = function (id) {
        var site = sites.find(id);
        if (!site) {
            ys.msgWarning('站点不存在，请刷新后重试');
            return;
        }
        ys.confirm('确认删除站点「' + site.SiteName + '」吗？已采集的资源不会被删除。', function () {
            ys.ajax({
                url: ns.api.deleteSite,
                type: 'post',
                data: { id: id },
                success: function (result) {
                    if (result.Tag === 1) {
                        ys.msgSuccess(result.Message || '删除成功');
                        sites.load();
                    } else {
                        ys.msgError(result.Message || '删除失败');
                    }
                }
            });
        });
    };

    // 视图内的内联 onclick 依赖这些全局函数名
    window.showSiteForm = sites.showForm;
    window.deleteSite = sites.remove;
    window.openSiteCategory = sites.openCategoryConfig;
})(window, jQuery);
