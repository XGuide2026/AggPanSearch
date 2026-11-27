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

    // 链接有效性标签：-1 未检测 / 1 有效 / 0 无效，空值表示状态未知（旧数据）
    function validTag(value) {
        if (ys.isNullOrEmpty(value)) {
            return { text: '状态未知', type: 'mute' };
        }
        if (String(value) === '-1') {
            return { text: '未检测', type: 'mute' };
        }
        return String(value) === '1'
            ? { text: '链接有效', type: 'ok' }
            : { text: '链接无效', type: 'warn' };
    }

    // 文件大小：数据库存 M 整数（采集分析按 1024 进制换算），这里还原为可读文本
    // 空 / 0 / 未分析（null）时返回空串不展示；>=1T 显示 x.xT，>=1G 显示 x.xG，>=1M 显示 xM，不足 1M 显示 xK
    function formatFileSize(fileSize) {
        var m = Number(fileSize);
        if (isNaN(m) || m <= 0) {
            return '';
        }
        if (m >= 1073741824) {
            return (m / 1073741824).toFixed(1) + 'T';
        }
        if (m >= 1024) {
            return (m / 1024).toFixed(1) + 'G';
        }
        if (m >= 1) {
            return Math.round(m) + 'M';
        }
        return Math.round(m * 1024) + 'K';
    }

    // 资源信息行：「文件个数 / 文件大小 / 资源有效期」三字段来自资源信息分析队列
    // （图片下载完成后打开网盘链接解析 .info-stat），组合为
    // 「共 N 个文件 6.2G 永久有效」显示在元信息行（collect-card-meta）上方；
    // 空字段跳过对应片段，三字段全部为空时不渲染该行
    function renderFileInfo(row) {
        var cl = ys.cardList;
        var parts = [];
        var count = row.FileCount === undefined || row.FileCount === null ? '' : String(row.FileCount);
        var size = formatFileSize(row.FileSize);
        var validity = $.trim(row.ShareValidityPeriod || '');
        if (count !== '' && count !== '0') {
            parts.push('共 ' + count + ' 个文件');
        }
        if (size !== '') {
            parts.push(size);
        }
        if (validity !== '') {
            parts.push(validity);
        }
        if (parts.length === 0) {
            return '';
        }
        return '<div class="collect-card-file-meta" title="' + cl.escHtml(parts.join(' ')) + '">' +
            '<i class="fa fa-database"></i> ' + cl.escHtml(parts.join(' ')) + '</div>';
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

        var cardTags = [categoryTag(row.SourceCategoryId), validTag(row.IsLinkValid)];
        return '<div class="collect-card" data-id="' + cl.escHtml(id) + '">' +
            cl.renderCover(cl.parseImages(row.CollectImageUrl)) +
            '<div class="collect-card-body">' +
            cl.renderTitle(name, '（未命名资源）', { saved: !!row.IsSave }) +
            // 资源信息行：文件个数 / 文件大小 / 资源有效期，显示在标签区上方
            renderFileInfo(row) +
            cl.renderTags(cardTags) +
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

    // 文件大小排序：三态点击循环
    //   状态  ''（默认）→ 'desc'（从大到小，↓ 高亮）→ 'asc'（从小到大，↑ 高亮）→ 回到默认
    // 箭头：向上 = 从小到大，向下 = 从大到小；第一次点击箭头向下高亮，第二次箭头向上高亮，
    // 第三次恢复默认（两个箭头均不高亮）。选择时状态写入 #fileSizeSort 隐藏框，并刷新表格。
    function bindFileSizeSorter() {
        var state = ''; // '' 默认 | 'desc' | 'asc'
        var $sorter = $('#fileSizeSorter');
        if (!$sorter.length) {
            return;
        }
        $sorter.on('click', function () {
            if (state === '') {
                state = 'desc';
            } else if (state === 'desc') {
                state = 'asc';
            } else {
                state = ''; // 第三次点击恢复默认排序
            }
            renderFileSizeSorter($sorter, state);
            $('#fileSizeSort').val(state);
            grid.search();
        });
    }

    // 按当前排序状态高亮对应箭头：desc 高亮向下箭头（从大到小），asc 高亮向上箭头（从小到大）
    function renderFileSizeSorter($sorter, state) {
        $sorter.removeClass('sorter-desc sorter-asc');
        if (state === 'desc') {
            $sorter.addClass('sorter-desc');
        } else if (state === 'asc') {
            $sorter.addClass('sorter-asc');
        }
    }

    grid.init = function () {
        // 链接状态：使用框架 ysComboBox，与系统其他页面搜索区下拉保持一致。
        // 查询条件下拉自动以 value=-1 作为「全部」；「未检测」用 -2 对应数据库的 -1
        $('#isLinkValid').ysComboBox({
            data: [
                { Key: '1', Value: '有效' },
                { Key: '0', Value: '无效' },
                { Key: '-2', Value: '未检测' }
            ],
            search: false
        });

        // 转存本地：1 已转存 / 0 未转存，「全部」由框架自动以 value=-1 提供，queryParams 里统一置空。
        // 已转存判定依据后端 pan_source 表存在原链接匹配的未软删除记录（与卡片上的「已转存」标记同一来源）。
        $('#isSave').ysComboBox({
            data: [
                { Key: '1', Value: '已转存' },
                { Key: '0', Value: '未转存' }
            ],
            search: false
        });

        // 采集时间：使用框架的 laydate 日期控件（_Index 布局已引入 laydate 5），
        // 取值格式固定为 yyyy-MM-dd，服务端按天区间（含当天）解析
        if (window.laydate) {
            laydate.render({ elem: '#startTime', format: 'yyyy-MM-dd', trigger: 'click' });
            laydate.render({ elem: '#endTime', format: 'yyyy-MM-dd', trigger: 'click' });
        }

        // 文件大小排序：单独一排的「文字 + 上下箭头」三态点击。
        // 状态：'' 默认不排序 → 'desc' 从大到小（↓ 高亮）→ 'asc' 从小到大（↑ 高亮）→ 'default' 恢复默认。
        // 状态写入带 col 的隐藏框，queryParams 里 getWebControls 自动收集为 file_size_sort，无需改 queryParams。
        bindFileSizeSorter();

        // 先取分类资源字典，再初始化筛选下拉与表格，避免列渲染时字典还没到
        loadCategoryOptions(function () {
            $('#sourceCategory').ysComboBox({ data: categoryOptions, key: 'Id', value: 'Name', search: false });
            initTable();
        });
        // 并行加载转存确认弹窗的目录名（配置表 quark_transfer_dir，失败不影响转存流程）
        loadTransferDir();
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
                if (queryString.is_save == '-1') {
                    queryString.is_save = '';
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

        // 页面刷新后若仍有转存任务在跑，恢复进度显示
        grid.resumeTransfer();
        // 资源图片下载队列：采集入库后由后台队列自动下载本地图片，持续轮询展示进度
        grid.startAutoImagePolling();
        // 资源文件分析队列：图片下载全部完成后由后台队列自动打开网盘链接解析三字段，持续轮询展示进度
        grid.startInfoAnalyzePolling();
    }

    // 工具栏按钮状态：单张卡片的修改 / 删除在卡片底部，工具栏负责批量操作：删除、转存、补图
    // 注意：不能命名为 resetToolbarStatus，aggpansearch-init.min.js 有同名全局函数会覆盖本页实现
    // count 由 ys.cardList 的 onSelection 回调传入，手工调用时可省略
    grid.updateToolbar = function (count) {
        if (count === undefined || count === null) {
            count = selectedRows().length;
        }

        // 转存任务运行中禁用删除，避免删掉正在处理的记录
        var busy = grid.isTransferRunning();
        $('#btnDelete').toggleClass('disabled', count === 0 || busy);
        $('#btnTransfer').toggleClass('disabled', count === 0 || grid.isTransferRunning());
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

    // ======================== 资源图片下载队列进度 ========================
    // 采集入库后由后台队列自动下载本地图片，无需手动触发；这里实时展示队列进度，
    // 让"采集完成但还没看到本地图片"期间用户能看到下载在跑、还剩多少。
    var AUTO_IMAGE_POLL_INTERVAL = 2000;
    var autoImageTimer = null;
    var autoImageFailures = 0;       // 连续查询失败的次数，用于避免错误提示闪烁/静默
    // 完成态编排标记：autoImageDoneHidden 防止 2 秒轮询重复刷新表格/重启隐藏定时器；
    // autoImageRunning 供隐藏定时器判断「隐藏期内是否又启动了新任务」，避免误收进度条
    var autoImageDoneHidden = false;
    var autoImageRunning = false;
    // autoImageSeenRunning：本页面会话内是否亲眼见过队列 running（或剩余待处理>0）。
    // 用于区分「本会话真实运行完成的任务」与「页面打开时后端遗留的历史完成汇总」。
    var autoImageSeenRunning = false;

    grid.startAutoImagePolling = function () {
        if (autoImageTimer) {
            return;
        }
        pollAutoImage();
        autoImageTimer = setInterval(pollAutoImage, AUTO_IMAGE_POLL_INTERVAL);
    };

    function pollAutoImage() {
        $.ajax({
            url: ns.api.autoImageProgress,
            type: 'get',
            dataType: 'json',
            success: function (obj) {
                autoImageFailures = 0;
                if (obj.Tag === 1 && obj.Data) {
                    renderAutoImageProgress(obj.Data);
                } else {
                    renderAutoImageError(obj.Message || '资源图片下载进度查询失败');
                }
            },
            error: function () {
                autoImageFailures++;
                if (autoImageFailures >= 2) {
                    renderAutoImageError('资源图片下载进度查询失败（网络/接口异常）');
                }
            }
        });
    }

    function renderAutoImageProgress(state) {
        var running = state.Status === 'running';
        var processed = state.Processed || 0;
        var updated = state.Updated || 0;
        var total = state.Total || 0;
        var remaining = Math.max(0, total - processed);

        // 处理中或仍有剩余图片待下载：实时进度条（封顶 95%）
        if (running || remaining > 0) {
            autoImageRunning = true;
            autoImageDoneHidden = false;
            autoImageSeenRunning = true;
            var percent = total > 0
                ? Math.min(95, Math.round(processed * 100 / total))
                : 0;
            showAutoImage();
            $('#autoImageProgressInner').addClass('active').css('width', percent + '%');
            var text = '资源图片下载：正在下载本地图片… 已处理 ' + processed + ' 张，入库 ' + updated
                + ' 张，剩余 ' + remaining + ' 张';
            if (state.Message) {
                text += '（' + state.Message + '）';
            }
            $('#autoImageProgressText').text(text);
            return;
        }

        // 本周期已完成过资源图片下载：显示 100% 完成汇总并刷新表格，5 秒后进度条自动消失
        if (processed > 0) {
            autoImageRunning = false;
            if (!autoImageSeenRunning) {
                // 页面打开时后端即处于完成态：属于历史遗留汇总，不显示进度条，
                // 仅刷新表格展示最新本地图片一次，并记忆以免后续轮询重复刷新。
                if (autoImageDoneHidden) {
                    return;
                }
                autoImageDoneHidden = true;
                hideAutoImageProgress();
                grid.search();
                return;
            }
            if (autoImageDoneHidden) {
                return;   // 已隐藏过：保持隐藏，不再重复展示/刷新表格/重启定时器
            }
            autoImageDoneHidden = true;
            showAutoImage();
            $('#autoImageProgressInner').removeClass('active').css('width', '100%');
            $('#autoImageProgressText').text(state.Message || '资源图片下载完成，本地图片已下载');
            grid.search();   // 下载完成后刷新表格，展示最新本地图片
            setTimeout(function () {
                if (autoImageRunning) {
                    return;   // 隐藏期内进入了新周期运行中：保持进度条显示
                }
                hideAutoImageProgress();
            }, 5000);
            return;
        }

        // 从未发生过下载：空闲时不显示进度条，避免页面出现空面板
        autoImageRunning = false;
        autoImageDoneHidden = false;
        hideAutoImageProgress();
    }

    function renderAutoImageError(message) {
        showAutoImage();
        $('#autoImageProgressInner').removeClass('active progress-bar-danger').addClass('progress-bar-danger').css('width', '100%');
        $('#autoImageProgressText').text(message);
    }

    function showAutoImage() {
        $('#autoImageProgressBar').show();
        ns.syncProgressPanel();
        $('#autoImageProgressInner').removeClass('progress-bar-danger');
    }

    function hideAutoImageProgress() {
        $('#autoImageProgressBar').hide();
        ns.syncProgressPanel();
        $('#autoImageProgressInner').removeClass('active progress-bar-danger').css('width', '0%');
    }

    // ======================== 资源文件分析队列进度 ========================
    // 图片下载全部完成后，由后台队列自动打开网盘链接解析三字段（文件个数/大小/有效期）；
    // 这里仿照图片下载队列实时展示进度，让用户能看到分析在跑、还剩多少，完成后自动刷新表格。
    var INFO_ANALYZE_POLL_INTERVAL = 2000;
    var infoAnalyzeTimer = null;
    var infoAnalyzeFailures = 0;       // 连续查询失败的次数，用于避免错误提示闪烁/静默
    // 完成态编排标记：infoAnalyzeDoneHidden 防止 2 秒轮询重复刷新表格/重启隐藏定时器；
    // infoAnalyzeRunning 供隐藏定时器判断「隐藏期内是否又启动了新任务」，避免误收进度条
    var infoAnalyzeDoneHidden = false;
    var infoAnalyzeRunning = false;
    // infoAnalyzeSeenRunning：本页面会话内是否亲眼见过队列 running（或剩余待处理>0）。
    // 用于区分「本会话真实运行完成的任务」与「页面打开时后端遗留的历史完成汇总」。
    var infoAnalyzeSeenRunning = false;

    grid.startInfoAnalyzePolling = function () {
        if (infoAnalyzeTimer) {
            return;
        }
        pollInfoAnalyze();
        infoAnalyzeTimer = setInterval(pollInfoAnalyze, INFO_ANALYZE_POLL_INTERVAL);
    };

    function pollInfoAnalyze() {
        $.ajax({
            url: ns.api.infoAnalyzeProgress,
            type: 'get',
            dataType: 'json',
            success: function (obj) {
                infoAnalyzeFailures = 0;
                if (obj.Tag === 1 && obj.Data) {
                    renderInfoAnalyzeProgress(obj.Data);
                } else {
                    renderInfoAnalyzeError(obj.Message || '资源文件分析进度查询失败');
                }
            },
            error: function () {
                infoAnalyzeFailures++;
                if (infoAnalyzeFailures >= 2) {
                    renderInfoAnalyzeError('资源文件分析进度查询失败（网络/接口异常）');
                }
            }
        });
    }

    function renderInfoAnalyzeProgress(state) {
        var running = state.Status === 'running';
        var done = state.Done || 0;
        var succeeded = state.Succeeded || 0;
        var skipped = state.Skipped || 0;
        var failed = state.Failed || 0;
        var total = state.Total || 0;
        var remaining = Math.max(0, total - done);

        // 处理中或仍有剩余资源待分析：实时进度条（封顶 95%）
        if (running || remaining > 0) {
            infoAnalyzeRunning = true;
            infoAnalyzeDoneHidden = false;
            infoAnalyzeSeenRunning = true;
            var percent = total > 0
                ? Math.min(95, Math.round(done * 100 / total))
                : 0;
            showInfoAnalyze();
            $('#infoAnalyzeProgressInner').addClass('active').css('width', percent + '%');
            var text = '资源文件分析：正在解析文件信息… 已分析 ' + done + ' 条，有效 ' + succeeded
                + ' 条，剩余 ' + remaining + ' 条';
            if (state.Message) {
                text += '（' + state.Message + '）';
            }
            $('#infoAnalyzeProgressText').text(text);
            return;
        }

        // 本周期已完成过资源文件分析：显示 100% 完成汇总并刷新表格，5 秒后进度条自动消失
        if (done > 0) {
            infoAnalyzeRunning = false;
            if (!infoAnalyzeSeenRunning) {
                // 页面打开时后端即处于完成态：属于历史遗留汇总，不显示进度条，
                // 仅刷新表格一次展示最新三字段，并记忆以免后续轮询重复刷新。
                if (infoAnalyzeDoneHidden) {
                    return;
                }
                infoAnalyzeDoneHidden = true;
                hideInfoAnalyzeProgress();
                grid.search();
                return;
            }
            if (infoAnalyzeDoneHidden) {
                return;   // 已隐藏过：保持隐藏，不再重复展示/刷新表格/重启定时器
            }
            infoAnalyzeDoneHidden = true;
            showInfoAnalyze();
            $('#infoAnalyzeProgressInner').removeClass('active').css('width', '100%');
            $('#infoAnalyzeProgressText').text(state.Message || '资源文件分析完成');
            grid.search();   // 分析完成后刷新表格，展示最新三字段
            setTimeout(function () {
                if (infoAnalyzeRunning) {
                    return;   // 隐藏期内进入了新周期运行中：保持进度条显示
                }
                hideInfoAnalyzeProgress();
            }, 5000);
            return;
        }

        // 从未发生过分析：空闲时不显示进度条，避免页面出现空面板
        infoAnalyzeRunning = false;
        infoAnalyzeDoneHidden = false;
        hideInfoAnalyzeProgress();
    }

    function renderInfoAnalyzeError(message) {
        showInfoAnalyze();
        $('#infoAnalyzeProgressInner').removeClass('active progress-bar-danger').addClass('progress-bar-danger').css('width', '100%');
        $('#infoAnalyzeProgressText').text(message);
    }

    function showInfoAnalyze() {
        $('#infoAnalyzeProgressBar').show();
        ns.syncProgressPanel();
        $('#infoAnalyzeProgressInner').removeClass('progress-bar-danger');
    }

    function hideInfoAnalyzeProgress() {
        $('#infoAnalyzeProgressBar').hide();
        ns.syncProgressPanel();
        $('#infoAnalyzeProgressInner').removeClass('active progress-bar-danger').css('width', '0%');
    }

    /* ===== 转存并分享（夸克网盘） ===== */
    var TRANSFER_JOB_KEY = 'panSourceTransferJobId';
    var transferTimer = null;
    var transferJobId = '';
    var transferStart = 0;
    var transferFailures = 0;
    // 转存确认弹窗显示的目标目录名：页面加载时从配置表（quark_transfer_dir）读取，失败时兜底「采集资源」
    var transferDirName = '采集资源';

    function loadTransferDir() {
        $.ajax({
            url: ns.api.transferDir,
            type: 'get',
            dataType: 'json',
            success: function (result) {
                if (result.Tag === 1 && result.Data) {
                    transferDirName = result.Data;
                }
            }
        });
    }

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
        // 选中的资源中存在「未分类」（SourceCategoryId 为空或 0）时阻止转存，
        // 提示用户先在编辑弹窗中为资源设置分类
        var unclassified = 0;
        for (var i = 0; i < rows.length; i++) {
            if (ys.isNullOrEmpty(rows[i].SourceCategoryId) || String(rows[i].SourceCategoryId) === '0') {
                unclassified++;
            }
        }
        if (unclassified > 0) {
            ys.msgError('选中的资源中存在 ' + unclassified + ' 条未分类资源，请先设置分类后再转存');
            return;
        }
        ys.confirm('1、你选中的 ' + rows.length + ' 条资源转存到夸克网盘「' + transferDirName + '」文件夹。<br/>2、生成的永久无提取码的分享链接<br/>3、非夸克链接会自动跳过。', function () {
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
        ns.syncProgressPanel();
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
        // 任务完成后延迟隐藏进度条：成功后 5 秒、失败/取消 8 秒；期间若又启动新任务则保持显示
        setTimeout(function () {
            if (!grid.isTransferRunning()) {
                $('#transferProgressBar').hide();
                ns.syncProgressPanel();
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
})(window, jQuery);
