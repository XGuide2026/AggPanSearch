/*
 * 站点采集 - 右侧采集资源卡片列表
 * 职责：卡片列表初始化与渲染、搜索分页、勾选与工具栏状态、详情查看、编辑、标记链接有效性、删除。
 * 说明：#gridTable 仍由 bootstrap-table 渲染并负责分页 / 勾选 / 请求，
 *       但列只保留「勾选列 + 单卡片列」，每一行即一张资源卡片，
 *       卡片由 aggpansearch-card-list.css 按瀑布流（多列不等高）排布。
 */
(function (window, $) {
    'use strict';

    var ns = window.PanSourceCollect;
    var grid = ns.grid = ns.grid || {};

    // 分类资源 Id → 名称：搜索下拉与卡片分类标签共用同一份数据，页面加载只请求一次
    var categoryOptions = [];
    var categoryNameMap = {};

    function loadCategoryOptions(done) {
        $.ajax({
            url: ns.api.categoryOptions,
            type: 'get',
            dataType: 'json',
            success: function (result) {
                var list = (result.Tag === 1 && result.Data) ? result.Data : [];
                categoryOptions = $.map(list, function (item) {
                    var id = item.Id === undefined ? item.id : item.Id;
                    var name = $.trim(item.Name === undefined ? item.name : item.Name);
                    if (!id || name === '') {
                        return null;
                    }
                    categoryNameMap[String(id)] = name;
                    return { Id: id, Name: name };
                });
                done();
            },
            error: function () {
                // 分类资源加载失败不阻塞列表，分类列降级显示 Id
                ys.msgWarning('分类资源加载失败，分类筛选与名称显示暂不可用');
                done();
            }
        });
    }

    // 分类标签：显示「分类资源」名称；空值表示未分类（红色提示需归位），分类被删除时保留 Id 便于排查
    function categoryTag(value) {
        if (ys.isNullOrEmpty(value) || String(value) === '0') {
            return { text: '未分类', type: 'danger' };
        }
        var name = categoryNameMap[String(value)];
        if (!name) {
            return { text: '已删除分类', type: 'mute', title: '分类资源 Id ' + value };
        }
        return { text: name, type: 'primary', title: name };
    }

    // 链接有效性标签：1 有效 / 其他无效，空值表示还没检测过
    function validTag(value) {
        if (ys.isNullOrEmpty(value)) {
            return { text: '状态未知', type: 'mute' };
        }
        return String(value) === '1'
            ? { text: '链接有效', type: 'ok' }
            : { text: '链接无效', type: 'warn' };
    }

    // 图片地址是否可用：可能只有空白项，拆分后为空数组即视为无图
    function hasImage(value) {
        return ys.cardList.parseImages(value).length > 0;
    }

    // 本地图片状态：卡片不展示本地图片，只给出「是否已下载到本地」的结论
    // 「无原图」表示采集时就没抓到图，无法下载；「未下载」表示有原图但还没下载到本机
    function localImageTag(row) {
        if (hasImage(row ? row.ImageUrl : '')) {
            return { text: '已下载', type: 'ok' };
        }
        if (hasImage(row ? row.CollectImageUrl : '')) {
            return { text: '未下载', type: 'warn' };
        }
        return { text: '无原图', type: 'mute' };
    }

    // 整张资源卡片：表格只保留这一列，行即卡片
    // 封面 / 标题 / 标签 / 简介 / 元信息 / 链接 / 操作条全部复用公共脚本 ys.cardList，
    // 本页只负责字段映射；所有文本由 ys.cardList 统一转义后再拼接
    function renderCard(value, row) {
        row = row || {};
        var cl = ys.cardList;
        var id = row.Id === undefined || row.Id === null ? '' : row.Id;
        var name = $.trim(row.ResourceName || '');
        var source = $.trim(row.CollectSource || '');
        var sourceText = source === '' ? '来源未知' : source;
        var timeText = ns.renderTime(row.BaseCreateTime);

        return '<div class="collect-card" data-id="' + cl.escHtml(id) + '">' +
            cl.renderCover(cl.parseImages(row.CollectImageUrl)) +
            '<div class="collect-card-body">' +
            cl.renderTitle(name, '（未命名资源）') +
            cl.renderTags([categoryTag(row.SourceCategoryId), validTag(row.IsLinkValid), localImageTag(row)]) +
            // 简介行数随内容自适应（上限约 12 行），超出时由 ys.cardList 在末行末尾补「查看完整」
            cl.renderIntro(row.ResourceIntro) +
            cl.renderMeta([
                { icon: 'fa-globe', text: sourceText, title: '采集来源：' + sourceText },
                { icon: 'fa-clock-o', text: timeText, title: '采集时间：' + timeText }
            ]) +
            cl.renderLink(row.ResourceUrl) +
            '</div>' +
            // 修改 / 删除直接作用于当前卡片，无需先勾选，复用视图里已有的全局函数
            cl.renderFoot(id, 'editCollectRow', 'deleteCollectRow') +
            '</div>';
    }

    grid.init = function () {
        // 链接状态：使用框架 ysComboBox，与系统其他页面搜索区下拉保持一致
        $('#isLinkValid').ysComboBox({
            data: [
                { Key: '1', Value: '有效' },
                { Key: '0', Value: '无效' }
            ],
            search: false
        });

        // 采集时间：使用框架的 laydate 日期控件（_Index 布局已引入 laydate 5），
        // 取值格式固定为 yyyy-MM-dd，服务端按天区间（含当天）解析
        if (window.laydate) {
            laydate.render({ elem: '#startTime', format: 'yyyy-MM-dd', trigger: 'click' });
            laydate.render({ elem: '#endTime', format: 'yyyy-MM-dd', trigger: 'click' });
        }

        // 先取分类资源字典，再初始化筛选下拉与表格，避免列渲染时字典还没到
        loadCategoryOptions(function () {
            $('#sourceCategory').ysComboBox({ data: categoryOptions, key: 'Id', value: 'Name', search: false });
            initTable();
        });
    };

    function initTable() {
        $('#gridTable').ysTable({
            url: ns.api.pageList,
            sortName: 'BaseCreateTime',
            sortOrder: 'desc',
            // 卡片列表没有表头：排序仍由服务端按采集时间倒序处理；
            // 同时关闭「列选择」与「视图切换」，避免切到 bootstrap-table 内置 cardView 打乱卡片布局
            showHeader: false,
            showColumns: false,
            showToggle: false,
            striped: false,
            // 点击卡片内的链接 / 按钮不勾选整行，勾选只由左上角复选框触发
            clickToSelect: false,
            // 列收敛为「勾选列 + 单卡片列」：分页、勾选、请求仍由 bootstrap-table 负责（#gridTable 保留）
            columns: [
                { checkbox: true, width: 30, valign: 'middle' },
                { field: 'Id', title: '资源', valign: 'top', formatter: renderCard }
            ],
            formatNoMatches: function () {
                return '暂无数据';
            },
            queryParams: function (params) {
                var pagination = $('#gridTable').ysTable('getPagination', params);
                var queryString = $('#searchDiv').getWebControls(pagination);
                // ysComboBox 的「全部」选项取值为 -1，这里按未选择处理
                if (queryString.collect_source == '-1') {
                    queryString.collect_source = '';
                }
                if (queryString.is_link_valid == '-1') {
                    queryString.is_link_valid = '';
                }
                if (queryString.source_category_id == '-1') {
                    queryString.source_category_id = '';
                }
                return queryString;
            }
        });

        // 卡片交互（勾选描边、「全选本页」、封面看大图、简介超长截断）统一由公共脚本处理，
        // 勾选数量变化时回调 updateToolbar 刷新工具栏按钮状态
        ys.cardList.bind({
            table: '#gridTable',
            selectAll: '#chkSelectAllPage',
            onSelection: grid.updateToolbar
        });

        // 页面刷新后若仍有转存 / 补图任务在跑，恢复进度显示
        grid.resumeTransfer();
        grid.resumeImage();
    }

    // 工具栏按钮状态：单张卡片的修改 / 删除在卡片底部，工具栏负责批量操作：删除、转存、补图
    // 注意：不能命名为 resetToolbarStatus，aggpansearch-init.min.js 有同名全局函数会覆盖本页实现
    // count 由 ys.cardList 的 onSelection 回调传入，手工调用时可省略
    grid.updateToolbar = function (count) {
        if (count === undefined || count === null) {
            count = selectedRows().length;
        }

        // 转存 / 补图任务运行中禁用删除，避免删掉正在处理的记录
        var busy = grid.isTransferRunning() || grid.isImageRunning();
        $('#btnDelete').toggleClass('disabled', count === 0 || busy);
        $('#btnTransfer').toggleClass('disabled', count === 0 || grid.isTransferRunning());
        $('#btnBackfillImage').toggleClass('disabled', grid.isImageRunning());
    };

    // 批量删除：拼接卡片勾选行的 Id，复用 remove 的确认与请求流程
    grid.removeSelected = function () {
        var rows = selectedRows();
        if (rows.length === 0) {
            ys.msgWarning('请选择要删除的资源');
            return;
        }
        grid.remove(ys.getIds(rows));
    };

    grid.search = function () {
        $('#gridTable').ysTable('search');
        grid.updateToolbar();
    };

    function selectedRows() {
        var rows = $('#gridTable').bootstrapTable('getSelections');
        return rows || [];
    }

    // 查看详情：id 由卡片底部按钮传入
    grid.showDetail = function (id) {
        if (!id) {
            ys.msgWarning('请选择要查看的资源');
            return;
        }
        ys.openDialog({
            title: '查看采集资源',
            content: ns.api.form + '?id=' + id + '&detail=1',
            width: '820px',
            height: '92%',
            btn: null
        });
    };

    // 新增传 bAdd=true；行内「修改」传当前行 Id
    grid.showForm = function (bAdd, id) {
        id = bAdd ? 0 : (id || 0);
        if (!bAdd && !id) {
            ys.msgWarning('请选择要修改的资源');
            return;
        }
        ys.openDialog({
            title: id > 0 ? '编辑采集资源' : '新增采集资源',
            content: ns.api.form + '?id=' + id,
            width: '820px',
            height: '92%',
            callback: function (index, layero) {
                var iframeWin = window[layero.find('iframe')[0]['name']];
                iframeWin.saveForm(index);
            }
        });
    };

    // 删除：ids 为卡片底部按钮传入的单行 Id（接口支持逗号分隔的多个 Id）
    grid.remove = function (ids) {
        if (!ids) {
            ys.msgWarning('请选择要删除的资源');
            return;
        }
        var count = String(ids).split(',').length;
        ys.confirm('确认要删除这' + (count > 1 ? ' ' + count + ' ' : '') + '条采集资源吗？', function () {
            ys.ajax({
                url: ns.api.deleteForm + '?ids=' + ids,
                type: 'post',
                success: function (obj) {
                    if (obj.Tag === 1) {
                        ys.msgSuccess(obj.Message);
                        grid.search();
                    } else {
                        ys.msgError(obj.Message);
                    }
                }
            });
        });
    };

    /* ===== 补图：把原图片下载到本机并回写本地图片 ===== */
    var IMAGE_JOB_KEY = 'panSourceImageJobId';
    var IMAGE_BACKFILL_LIMIT = 50;
    var imageTimer = null;
    var imageJobId = '';
    var imageStart = 0;
    var imageFailures = 0;

    // 任务运行中以「取消按钮可见」为准，避免刷新页面后状态错乱
    grid.isImageRunning = function () {
        return $('#btnCancelImageBackfill').is(':visible');
    };

    grid.resumeImage = function () {
        var jobId = sessionStorage.getItem(IMAGE_JOB_KEY);
        if (jobId) {
            startImagePolling(jobId);
        }
    };

    // 有选中行只补选中记录，未选中则补最近缺少本地图片的前 50 条
    grid.backfillImage = function () {
        var rows = selectedRows();
        var ids = rows.length > 0 ? ys.getIds(rows) : '';
        var tip = rows.length > 0
            ? '确认给选中的 ' + rows.length + ' 条资源补图吗？会把原图片下载到本机并回写本地图片，下载失败的原图会被剔除。'
            : '未选择记录，将为最近缺少本地图片的前 ' + IMAGE_BACKFILL_LIMIT + ' 条资源补图，是否继续？';
        ys.confirm(tip, function () {
            ys.ajax({
                url: ns.api.backfillImage,
                type: 'post',
                data: { ids: ids, limit: IMAGE_BACKFILL_LIMIT },
                success: function (obj) {
                    if (obj.Tag === 1 && obj.Data) {
                        layer.msg('补图任务已提交，正在后台下载图片…', { icon: 1, time: 2000, shift: 5 });
                        sessionStorage.setItem(IMAGE_JOB_KEY, obj.Data);
                        startImagePolling(obj.Data);
                    } else {
                        ys.msgError(obj.Message || '补图任务提交失败');
                    }
                }
            });
        });
    };

    function startImagePolling(jobId) {
        stopImageTimer();
        imageJobId = jobId || '';
        imageStart = Date.now();
        imageFailures = 0;
        $('#btnBackfillImage').addClass('disabled');
        $('#imageProgressBar').show();
        $('#imageProgressInner').addClass('active').css('width', '5%');
        $('#imageProgressText').text('正在下载本地图片…');
        $('#btnCancelImageBackfill').show().removeClass('disabled');
        pollImage();
        imageTimer = setInterval(pollImage, 1500);
    }

    function stopImageTimer() {
        if (imageTimer) {
            clearInterval(imageTimer);
            imageTimer = null;
        }
    }

    function pollImage() {
        // 兜底上限：单次补图最多 50 条，30 分钟仍未结束则停止轮询
        if (Date.now() - imageStart > 30 * 60 * 1000) {
            finishImage('补图进度查询超时，请刷新列表查看结果', false, null);
            return;
        }
        $.ajax({
            url: ns.api.imageProgress,
            type: 'get',
            data: { jobId: imageJobId },
            dataType: 'json',
            success: function (result) {
                if (result.Tag !== 1 || !result.Data) {
                    finishImage(result.Message || '补图任务已结束', false, null);
                    return;
                }
                imageFailures = 0;
                var state = result.Data;
                imageJobId = state.JobId || imageJobId;
                sessionStorage.setItem(IMAGE_JOB_KEY, imageJobId);
                renderImageProgress(state);
                if (state.Status === 'succeeded') {
                    finishImage(state.Message, true, state);
                } else if (state.Status === 'failed' || state.Status === 'canceled') {
                    finishImage(state.Message, false, state);
                }
            },
            error: function () {
                imageFailures++;
                if (imageFailures >= 5) {
                    finishImage('补图进度查询失败，请刷新页面查看结果', false, null);
                }
            }
        });
    }

    function renderImageProgress(state) {
        var total = state.Total || 0;
        var done = state.Done || 0;
        var percent = total > 0 ? Math.min(100, Math.round(done * 100 / total)) : 5;
        var text = '已处理 ' + done + '/' + total +
            ' 条，回写 ' + (state.Updated || 0) +
            '，跳过 ' + (state.Skipped || 0) +
            '，剔除失效图片 ' + (state.FailedImages || 0) + ' 张';
        $('#imageProgressInner').css('width', percent + '%');
        $('#imageProgressText').text(text);
    }

    // 补图结束刷新列表：本地图片列需要重新取数才能看到结果
    function finishImage(message, succeeded, state) {
        stopImageTimer();
        sessionStorage.removeItem(IMAGE_JOB_KEY);
        imageJobId = '';
        $('#btnCancelImageBackfill').hide();
        $('#imageProgressInner').removeClass('active');
        if (succeeded) {
            $('#imageProgressInner').css('width', '100%');
        }
        if (message) {
            $('#imageProgressText').text(message);
            layer.msg(message, { icon: succeeded ? 1 : 0, time: 6000, shift: 5 });
        }
        setTimeout(function () {
            if (!grid.isImageRunning()) {
                $('#imageProgressBar').hide();
            }
        }, succeeded ? 4000 : 8000);
        grid.search();
    }

    grid.cancelImageBackfill = function () {
        if (!imageJobId) {
            return;
        }
        ys.confirm('确认取消补图任务吗？已回写的本地图片会保留。', function () {
            ys.ajax({
                url: ns.api.cancelImageBackfill,
                type: 'post',
                data: { jobId: imageJobId },
                success: function (obj) {
                    if (obj.Tag === 1) {
                        ys.msgSuccess(obj.Message);
                    } else {
                        ys.msgError(obj.Message);
                    }
                }
            });
        });
    };

    /* ===== 转存并分享（夸克网盘） ===== */
    var TRANSFER_JOB_KEY = 'panSourceTransferJobId';
    var transferTimer = null;
    var transferJobId = '';
    var transferStart = 0;
    var transferFailures = 0;

    // 任务运行中以「取消按钮可见」为准，避免刷新页面后状态错乱
    grid.isTransferRunning = function () {
        return $('#btnCancelTransfer').is(':visible');
    };

    grid.resumeTransfer = function () {
        var jobId = sessionStorage.getItem(TRANSFER_JOB_KEY);
        if (jobId) {
            startTransferPolling(jobId);
        }
    };

    grid.transfer = function () {
        var rows = selectedRows();
        if (rows.length === 0) {
            ys.msgWarning('请先选择要转存的资源');
            return;
        }
        ys.confirm('确认把选中的 ' + rows.length + ' 条资源转存到夸克网盘「默认转存目录/资源分类」文件夹，并生成永久无提取码的分享链接吗？非夸克链接会自动跳过。', function () {
            ys.ajax({
                url: ns.api.transfer,
                type: 'post',
                data: { ids: ys.getIds(rows) },
                success: function (obj) {
                    if (obj.Tag === 1 && obj.Data) {
                        layer.msg('转存任务已提交，正在后台转存并分享…', { icon: 1, time: 2000, shift: 5 });
                        sessionStorage.setItem(TRANSFER_JOB_KEY, obj.Data);
                        startTransferPolling(obj.Data);
                    } else {
                        ys.msgError(obj.Message || '转存任务提交失败');
                    }
                }
            });
        });
    };

    function startTransferPolling(jobId) {
        stopTransferTimer();
        transferJobId = jobId || '';
        transferStart = Date.now();
        transferFailures = 0;
        $('#btnTransfer').addClass('disabled');
        $('#transferProgressBar').show();
        $('#transferProgressInner').addClass('active').css('width', '5%');
        $('#transferProgressText').text('正在转存并分享…');
        $('#btnCancelTransfer').show().removeClass('disabled');
        pollTransfer();
        transferTimer = setInterval(pollTransfer, 1500);
    }

    function stopTransferTimer() {
        if (transferTimer) {
            clearInterval(transferTimer);
            transferTimer = null;
        }
    }

    function pollTransfer() {
        // 单条转存最长 30 分钟，批量可能持续数小时，这里只做兜底上限保护
        if (Date.now() - transferStart > 6 * 60 * 60 * 1000) {
            finishTransfer('转存进度查询超时，请刷新列表查看结果', false, null);
            return;
        }
        $.ajax({
            url: ns.api.transferProgress,
            type: 'get',
            data: { jobId: transferJobId },
            dataType: 'json',
            success: function (result) {
                if (result.Tag !== 1 || !result.Data) {
                    finishTransfer(result.Message || '转存任务已结束', false, null);
                    return;
                }
                transferFailures = 0;
                var state = result.Data;
                transferJobId = state.JobId || transferJobId;
                sessionStorage.setItem(TRANSFER_JOB_KEY, transferJobId);
                renderTransfer(state);
                if (state.Status === 'succeeded') {
                    finishTransfer(state.Message, true, state);
                } else if (state.Status === 'failed' || state.Status === 'canceled') {
                    finishTransfer(state.Message, false, state);
                }
            },
            error: function () {
                transferFailures++;
                if (transferFailures >= 5) {
                    finishTransfer('转存进度查询失败，请刷新页面查看结果', false, null);
                }
            }
        });
    }

    function renderTransfer(state) {
        var total = state.Total || 0;
        var done = state.Done || 0;
        var percent = total > 0 ? Math.min(100, Math.round(done * 100 / total)) : 5;
        var text = '已处理 ' + done + '/' + total +
            ' 条，成功 ' + (state.Succeeded || 0) +
            '，跳过 ' + (state.Skipped || 0) +
            '，失败 ' + (state.Failed || 0);
        $('#transferProgressInner').css('width', percent + '%');
        $('#transferProgressText').text(text);
    }

    // succeeded 仅控制「成功」的视觉效果：进度条置 100%、成功图标、更短的隐藏延时。
    // 注意：无论成功与否都不刷新整张表格（表格没有转存状态列），否则会重置分页、排序、
    // 滚动位置并清空勾选，打断用户当前的浏览；需要最新数据时由用户自行点「搜索」。
    function finishTransfer(message, succeeded, state) {
        stopTransferTimer();
        sessionStorage.removeItem(TRANSFER_JOB_KEY);
        transferJobId = '';
        $('#btnCancelTransfer').hide();
        $('#transferProgressInner').removeClass('active');
        if (succeeded) {
            $('#transferProgressInner').css('width', '100%');
        }
        var detail = buildTransferDetail(state);
        if (message) {
            $('#transferProgressText').text(message + detail);
            layer.msg(message, { icon: succeeded ? 1 : 0, time: 6000, shift: 5 });
        }
        setTimeout(function () {
            if (!grid.isTransferRunning()) {
                $('#transferProgressBar').hide();
            }
        }, succeeded ? 5000 : 8000);
        // 不刷新表格，但选中行仍然有效，这里恢复「转存 / 删除 / 标记」等按钮可用状态
        grid.updateToolbar();
    }

    // 失败/跳过条目的简要原因，最多列 3 条，避免提示过长
    function buildTransferDetail(state) {
        if (!state || !state.Items || state.Items.length === 0) {
            return '';
        }
        var parts = [];
        for (var i = 0; i < state.Items.length && parts.length < 3; i++) {
            var item = state.Items[i];
            if (item.Status === 'failed' || item.Status === 'skipped') {
                parts.push((item.Title || ('Id ' + item.Id)) + '：' + (item.Message || ''));
            }
        }
        return parts.length > 0 ? '（' + parts.join('；') + '）' : '';
    }

    grid.cancelTransfer = function () {
        if (!transferJobId) {
            return;
        }
        ys.confirm('确认取消转存任务吗？已生成的本地资源会保留。', function () {
            ys.ajax({
                url: ns.api.cancelTransfer,
                type: 'post',
                data: { jobId: transferJobId },
                success: function (obj) {
                    if (obj.Tag === 1) {
                        ys.msgSuccess(obj.Message);
                    } else {
                        ys.msgError(obj.Message);
                    }
                }
            });
        });
    };

    // 视图内的内联 onclick 依赖这些全局函数名
    window.searchGrid = grid.search;
    window.showSaveForm = grid.showForm;
    // 卡片底部按钮：传入当前卡片 Id
    window.editCollectRow = function (id) { grid.showForm(false, id); };
    window.showCollectRow = grid.showDetail;
    window.deleteCollectRow = grid.remove;
    window.deleteSelectedRows = grid.removeSelected;
    window.transferToLocal = grid.transfer;
    window.cancelTransfer = grid.cancelTransfer;
    window.backfillImage = grid.backfillImage;
    window.cancelImageBackfill = grid.cancelImageBackfill;
})(window, jQuery);
