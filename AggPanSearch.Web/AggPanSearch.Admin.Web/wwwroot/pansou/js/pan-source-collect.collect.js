/*
 * 站点采集 - 采集流程
 * 职责：打开采集参数页、提交任务、进度轮询、取消采集、刷新后恢复进度。
 * 说明：采集参数（方式 tab、地址、页数）在独立页面 PanSourceCollectParamForm 中维护。
 */
(function (window, $) {
    'use strict';

    var ns = window.PanSourceCollect;
    var collect = ns.collect = ns.collect || {};

    var pollTimer = null;
    var pollStart = 0;
    var pollFailures = 0;

    // 打开「采集参数」页面：采集方式用 tab 切换，地址与页数在该页面内填写并提交
    collect.open = function (id) {
        if (ns.state.running) {
            ys.msgWarning('当前已有采集任务在执行，请等待完成后再试');
            return;
        }
        var site = ns.sites.find(id);
        if (!site) {
            ys.msgWarning('站点不存在，请刷新后重试');
            return;
        }

        // 顺序约束：先「配置分类」再「采集」
        var categoryCount = 0;
        $.each(site.Categories || [], function (i, item) {
            if ($.trim(item.Name || '') !== '') {
                categoryCount++;
            }
        });
        if (categoryCount === 0) {
            ys.msgWarning('请先点击「配置分类」完成分类配置，再开始采集');
            return;
        }

        ys.openDialog({
            title: '采集参数 - ' + (site.SiteName || ''),
            content: ns.api.paramForm + '?siteId=' + encodeURIComponent(id),
            width: '680px',
            height: '560px',
            btn: ['开始采集', '取消'],
            callback: function (index, layero) {
                // 校验与提交都在参数页面内完成，这里只负责把结果透传给 layer
                var iframeWin = window[layero.find('iframe')[0]['name']];
                return iframeWin.submitForm(index);
            }
        });
    };

    collect.start = function (siteId, args) {
        // 记住本次使用的模式，下次打开参数页面时默认选中
        ns.state.mode = args.mode;
        sessionStorage.setItem(ns.state.modeKey, args.mode);
        ys.ajax({
            url: ns.api.startCollect,
            type: 'post',
            data: {
                siteId: siteId,
                mode: args.mode,
                detailUrl: args.detailUrl,
                maxPages: args.maxPages,
                // 详情页采集不提交列表页地址，避免服务端做无意义的地址校验
                listUrl: args.mode !== 0 ? args.listUrl : '',
                // 分页链接模板仅分页列表采集使用
                pageTemplate: args.mode === 2 ? args.pageTemplate : ''
            },
            success: function (result) {
                if (result.Tag === 1 && result.Data) {
                    layer.msg('采集任务已提交，正在后台采集…', { icon: 1, time: 2000, shift: 5 });
                    sessionStorage.setItem(ns.state.jobKey, result.Data);
                    startPolling(result.Data);
                } else {
                    ys.msgError(result.Message || '采集任务提交失败');
                }
            },
            error: function (xhr) {
                ys.msgError('采集请求失败：' + (xhr.statusText || '网络异常'));
            }
        });
    };

    function startPolling(jobId) {
        stopTimer();
        ns.state.jobId = jobId || '';
        pollStart = Date.now();
        pollFailures = 0;
        ns.sites.toggleCollectButtons(true);
        $('#collectProgressBar').show();
        $('#collectProgressInner').addClass('active').css('width', '5%');
        $('#btnCancelCollect').show().removeClass('disabled');
        poll();
        pollTimer = setInterval(poll, 1500);
    }

    function stopTimer() {
        if (pollTimer) {
            clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function poll() {
        if (Date.now() - pollStart > 40 * 60 * 1000) {
            finish('采集进度查询超时，请刷新列表查看结果', false);
            return;
        }
        $.ajax({
            url: ns.api.progress,
            type: 'get',
            data: { jobId: ns.state.jobId },
            dataType: 'json',
            success: function (result) {
                if (result.Tag !== 1 || !result.Data) {
                    finish(result.Message || '采集任务已结束', false);
                    return;
                }
                pollFailures = 0;
                var state = result.Data;
                ns.state.jobId = state.JobId || ns.state.jobId;
                render(state);
                if (state.Status === 'succeeded') {
                    finish(state.Message, true);
                } else if (state.Status === 'failed' || state.Status === 'canceled') {
                    finish(state.Message, false);
                }
            },
            error: function () {
                pollFailures++;
                if (pollFailures >= 5) {
                    finish('采集进度查询失败，请刷新页面查看结果', false);
                }
            }
        });
    }

    function render(state) {
        var done = state.PagesDone || 0;
        var total = state.PagesTotal || 0;
        // 模板分页无法预知总页数，按已处理页数给一个渐进进度，避免进度条长时间停在同一位置
        var percent = total > 0
            ? Math.min(100, Math.round(done * 100 / total))
            : Math.min(90, 5 + done * 5);
        var pageText = total > 0 ? ('已完成 ' + done + '/' + total + ' 页') : ('已处理 ' + done + ' 页');
        var text = (state.SiteName ? '【' + state.SiteName + '】' : '') + pageText +
            '，发现 ' + (state.Found || 0) + ' 条，入库 ' + (state.Inserted || 0) + ' 条' +
            '，重复 ' + (state.Duplicated || 0) + ' 条';
        if (state.Skipped) {
            text += '，无效 ' + state.Skipped + ' 条';
        }
        $('#collectProgressInner').css('width', percent + '%');
        $('#collectProgressText').text(text);
    }

    function finish(message, refresh) {
        stopTimer();
        sessionStorage.removeItem(ns.state.jobKey);
        ns.sites.toggleCollectButtons(false);
        $('#btnCancelCollect').hide();
        $('#collectProgressInner').removeClass('active');
        if (refresh) {
            $('#collectProgressInner').css('width', '100%');
        }
        if (message) {
            $('#collectProgressText').text(message);
            layer.msg(message, { icon: refresh ? 1 : 0, time: 5000, shift: 5 });
        }
        if (refresh) {
            ns.grid.search();
        }
        setTimeout(function () {
            // 期间若又启动了新任务，则保持进度条显示
            if (!ns.state.running) {
                $('#collectProgressBar').hide();
            }
        }, refresh ? 5000 : 8000);
    }

    collect.cancel = function () {
        if (!ns.state.jobId) {
            return;
        }
        ys.confirm('确认取消当前采集任务吗？已入库的数据会保留。', function () {
            ys.ajax({
                url: ns.api.cancel,
                type: 'post',
                data: { jobId: ns.state.jobId },
                success: function (result) {
                    if (result.Tag === 1) {
                        ys.msgSuccess(result.Message);
                    } else {
                        ys.msgError(result.Message);
                    }
                }
            });
        });
    };

    // 页面刷新后若后台任务仍在运行，恢复进度显示
    collect.resume = function () {
        var jobId = sessionStorage.getItem(ns.state.jobKey) || '';
        $.ajax({
            url: ns.api.progress,
            type: 'get',
            data: { jobId: jobId },
            dataType: 'json',
            success: function (result) {
                if (result.Tag === 1 && result.Data && result.Data.Status === 'running') {
                    startPolling(result.Data.JobId);
                    render(result.Data);
                } else if (jobId) {
                    sessionStorage.removeItem(ns.state.jobKey);
                }
            }
        });
    };

    // 视图内的内联 onclick 依赖这些全局函数名
    window.collectSite = collect.open;
    window.cancelCollect = collect.cancel;
})(window, jQuery);
