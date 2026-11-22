(function () {
    // 公众号引流诊断：URL 带 ?wxdebug=1 时清除"已激活"记忆（localStorage 键与 _Layout.cshtml 保持一致），
    // 这样刷新后点「查看资源」必定弹窗，便于反复验证 mode=2；控制台同时打印配置与状态。
    if (/[?&]wxdebug=1\b/.test(location.search)) {
        try { localStorage.removeItem('wx_traffic_activated'); } catch (e) { }
        console.info('[wxTraffic] debug on, SITE_WX_TRAFFIC =', window.SITE_WX_TRAFFIC,
            'activated =', (function () { try { return localStorage.getItem('wx_traffic_activated'); } catch (e) { return 'n/a'; } })());
    }
    // 手动复位引流激活状态：控制台执行 wxTrafficReset() 后再点「查看资源」即可重新看到弹窗
    window.wxTrafficReset = function () {
        try { localStorage.removeItem('wx_traffic_activated'); return 'cleared'; } catch (e) { return 'failed'; }
    };

    const { createApp, ref, onMounted, onUnmounted } = Vue;
    const { ElButton, ElMessage  } = ElementPlus;
    const app = createApp({
        setup() {
            // 定义响应式数据
            const elementOpacity = ref(0);
            const scrollThreshold = ref(150); // 动态设置的滚动阈值
            const keyword = ref(window.__UF__.keyword ?? '');
            const layerVisible = ref(false);
            const content = ref('');
            const load = ref(false)
            const drawer = ref(false)
            const rankList = ref([]);
            const rankDj = ref([]);
            const is_m = ref(0);
            const newList = ref([]);
            const QList = ref([]);
            const QLoading = ref(false);
            const total_result = ref(0);
            const currentSource = ref(0);
            const dialogUrl = ref(false);
            const dialogLoading = ref(false);
            const dialogItem = ref({});
            const is_type = ref(0);
            const pc_type = ref(0);
            const panTypeMap = ref(window.__UF__.panTypeMap || {});
            
            // 主题切换
            const currentTheme = ref('red');
            const showThemeDropdown = ref(false);
            const themeSwitcherRef = ref(null);
            
            // 切换下拉显示
            const toggleThemeDropdown = () => {
                showThemeDropdown.value = !showThemeDropdown.value;
            };
            
            // 切换主题
            const switchTheme = (theme) => {
                // 后台"切换主题"关闭了访客切换时不再响应
                if (window.__UF_THEME_SWITCH__ === false) {
                    return;
                }
                currentTheme.value = theme;
                document.documentElement.setAttribute('data-theme', theme);
                localStorage.setItem('app-theme', theme);
                showThemeDropdown.value = false;
                showMessage('主题已切换', 'success');
            };
            
            // 初始化主题：以页面首屏已写入的 data-theme（后台配置）为准
            const initTheme = () => {
                const appliedTheme = document.documentElement.getAttribute('data-theme') || 'red';
                currentTheme.value = appliedTheme;
            };
            
            // 点击外部关闭下拉
            const closeThemeDropdown = (e) => {
                if (themeSwitcherRef.value && !themeSwitcherRef.value.contains(e.target)) {
                    showThemeDropdown.value = false;
                }
            };
            
            // 公共消息方法
            const showMessage = (message, type = 'info') => {
                ElMessage({
                    message,
                    type,
                    plain: true,
                });
            };


             // 滚动监听方法
            const handleScroll = () => {
                const scrollTop = window.scrollY || document.documentElement.scrollTop;
                elementOpacity.value = scrollTop >= scrollThreshold.value
                    ? Math.min((scrollTop - scrollThreshold.value) / 100, 1)
                    : Math.max(1 - (scrollThreshold.value - scrollTop) / 100, 0);
    
                const boxElement = document.querySelector('.listBox .screen .fixed .box');
                if (boxElement && boxElement.style.display === 'block' && is_m.value) {
                    boxElement.style.display = 'none';
                }
            };

            // 当前选中的网盘类型；Search 页面（非首页）始终保持默认选中"夸克网盘"（is_type=0），
            // 首页保持默认全部类型；若夸克网盘未在后台启用（panTypeMap 无 key 0），回退选中第一个可用类型
            var currentPanType = '';
            try {
                if (!(window.__UF__ && window.__UF__.isHome)) {
                    var __pm = (window.__UF__ && window.__UF__.panTypeMap) || {};
                    var __keys = Object.keys(__pm);
                    currentPanType = (__keys.indexOf('0') >= 0) ? '0' : (__keys.length ? String(__keys[0]) : '');
                }
            } catch (e) { currentPanType = ''; }
            var searchResults = [];

            // 搜索结果缓存配置（10分钟）
            const SEARCH_CACHE_DURATION = 10 * 60 * 1000; // 10分钟

            // 获取搜索结果缓存
            const getSearchCache = (query, panType) => {
                try {
                    const cacheKey = 'search_cache_' + encodeURIComponent(query) + '_' + panType;
                    const cacheData = localStorage.getItem(cacheKey);
                    if (cacheData) {
                        const { data, timestamp, type, checkResults } = JSON.parse(cacheData);
                        if (Date.now() - timestamp < SEARCH_CACHE_DURATION) {
                            return { data: data, type: type, checkResults: checkResults || {} };
                        }
                    }
                } catch (e) {
                }
                return null;
            };

            // 保存搜索结果缓存
            const setSearchCache = (query, panType, data, checkResults) => {
                try {
                    const cacheKey = 'search_cache_' + encodeURIComponent(query) + '_' + panType;
                    const cacheData = {
                        data: data,
                        type: panType,
                        timestamp: Date.now(),
                        checkResults: checkResults || {}
                    };
                    localStorage.setItem(cacheKey, JSON.stringify(cacheData));
                } catch (e) {
                }
            };
            
            // ===== 链接有效性检测结果缓存（URL 级）=====
            // 检测结果只和 URL 有关，与关键词、网盘类型无关：按 URL 存一份即可跨搜索、跨类型、跨会话复用。
            // 一级缓存是内存 Map（读写 O(1)），二级是 localStorage（持久化）；
            // 落盘做节流批量写，避免旧实现每条结果都 JSON.stringify 整张表（O(n²)）。
            var LINK_CHECK_CACHE_KEY = 'link_check_cache_v1';
            var LINK_CHECK_TTL_VALID = 24 * 60 * 60 * 1000;   // 有效：24 小时
            var LINK_CHECK_TTL_INVALID = 6 * 60 * 60 * 1000;   // 失效：6 小时（链接可能恢复）
            var LINK_CHECK_TTL_ERROR = 3 * 60 * 1000;          // 检测失败：3 分钟（允许自动重试）
            var LINK_CHECK_MAX_ENTRIES = 3000;                 // 条目上限，超限按时间最旧淘汰
            // 同一 URL 的 error 状态最短重测间隔：切换类型/翻页重建 DOM 时不会对刚失败的链接反复轰炸检测接口
            var LINK_CHECK_ERROR_RETRY_GATE = 2 * 60 * 1000;

            var linkCheckMap = null;        // url -> { v: true|false|null, e: 错误信息, t: 时间戳 }
            var linkCheckDirty = false;
            var linkCheckFlushTimer = null;

            // 载入缓存到内存（只读一次 localStorage）
            function loadLinkCheckCache() {
                if (linkCheckMap) return linkCheckMap;
                linkCheckMap = new Map();
                try {
                    var raw = localStorage.getItem(LINK_CHECK_CACHE_KEY);
                    if (raw) {
                        var obj = JSON.parse(raw) || {};
                        Object.keys(obj).forEach(function (url) {
                            var rec = obj[url];
                            if (rec && typeof rec.t === 'number') linkCheckMap.set(url, rec);
                        });
                    }
                } catch (e) {
                }
                return linkCheckMap;
            }

            function getLinkCheckTtl(rec) {
                if (rec.v === true) return LINK_CHECK_TTL_VALID;
                if (rec.v === false) return LINK_CHECK_TTL_INVALID;
                return LINK_CHECK_TTL_ERROR;
            }

            // 读取某条 URL 的检测结果，过期或不存在返回 null
            function getCachedLinkCheck(url) {
                if (!url) return null;
                var map = loadLinkCheckCache();
                var rec = map.get(url);
                if (!rec) return null;
                if (Date.now() - rec.t > getLinkCheckTtl(rec)) {
                    map.delete(url);
                    linkCheckDirty = true;
                    scheduleLinkCheckFlush();
                    return null;
                }
                return rec;
            }

            // 写入某条 URL 的检测结果（先进内存，再节流落盘）
            function setCachedLinkCheck(url, result) {
                if (!url) return;
                var map = loadLinkCheckCache();
                map.set(url, { v: result.valid, e: result.error || undefined, t: Date.now() });
                linkCheckDirty = true;
                scheduleLinkCheckFlush();
            }

            function scheduleLinkCheckFlush() {
                if (linkCheckFlushTimer) return;
                linkCheckFlushTimer = setTimeout(function () {
                    linkCheckFlushTimer = null;
                    flushLinkCheckCache();
                }, 800);
            }

            // 批量落盘：顺带清理过期项、按上限淘汰最旧项，防止 localStorage 无限膨胀
            function flushLinkCheckCache() {
                if (!linkCheckDirty || !linkCheckMap) return;
                linkCheckDirty = false;
                try {
                    var now = Date.now();
                    var entries = [];
                    linkCheckMap.forEach(function (rec, url) {
                        if (now - rec.t > getLinkCheckTtl(rec)) {
                            linkCheckMap.delete(url);
                            return;
                        }
                        entries.push([url, rec]);
                    });
                    if (entries.length > LINK_CHECK_MAX_ENTRIES) {
                        entries.sort(function (a, b) { return a[1].t - b[1].t; });
                        entries.slice(0, entries.length - LINK_CHECK_MAX_ENTRIES).forEach(function (p) {
                            linkCheckMap.delete(p[0]);
                        });
                        entries = entries.slice(entries.length - LINK_CHECK_MAX_ENTRIES);
                    }
                    var obj = {};
                    entries.forEach(function (p) { obj[p[0]] = p[1]; });
                    localStorage.setItem(LINK_CHECK_CACHE_KEY, JSON.stringify(obj));
                } catch (e) {
                    // 隐私模式/禁用存储/超配额时静默降级为纯内存缓存，功能不受影响
                }
            }

            // 清空 URL 级检测缓存，返回被清掉的条数
            function clearLinkCheckCache() {
                var count = linkCheckMap ? linkCheckMap.size : 0;
                linkCheckMap = new Map();
                linkCheckDirty = false;
                if (linkCheckFlushTimer) {
                    clearTimeout(linkCheckFlushTimer);
                    linkCheckFlushTimer = null;
                }
                try { localStorage.removeItem(LINK_CHECK_CACHE_KEY); } catch (e) {}
                return count;
            }
            window.clearAllLinkCheckCache = clearLinkCheckCache;

            // 关闭/切后台时把未落盘的结果刷一次，避免最后几条检测结果丢失
            window.addEventListener('pagehide', flushLinkCheckCache);
            document.addEventListener('visibilitychange', function () {
                if (document.visibilityState === 'hidden') flushLinkCheckCache();
            });

            // 清空搜索结果缓存
            const clearSearchCache = (query, panType) => {
                try {
                    const cacheKey = 'search_cache_' + encodeURIComponent(query) + '_' + panType;
                    localStorage.removeItem(cacheKey);
                } catch (e) {
                }
            };
            
            // 页面加载时设置默认选中状态
            document.addEventListener('DOMContentLoaded', function() {
                // 设置桌面端标签选中
                var desktopTabs = document.querySelectorAll('.pan-type-tab');
                desktopTabs.forEach(function(tab) {
                    if (tab.getAttribute('data-type') === currentPanType) {
                        tab.classList.add('active');
                    } else {
                        tab.classList.remove('active');
                    }
                });
                // 设置移动端下拉列表选中
                var mobileSelect = document.querySelector('.pan-type-select');
                if (mobileSelect) {
                    mobileSelect.value = currentPanType;
                }

                // 移动端按钮组选中态与当前类型保持一致
                document.querySelectorAll('.pan-type-mobile-btn').forEach(function(btn) {
                    if (btn.getAttribute('data-type') === currentPanType) {
                        btn.classList.add('active');
                    } else {
                        btn.classList.remove('active');
                    }
                });

                // 搜索框图标与当前网盘类型保持一致（默认未选中任何类型时为放大镜）
                window.updateSearchPanIcon(currentPanType);
            });

            // ===== 搜索统计（今日搜索 / 总搜索量）=====
            // 数字变化频率低，做三层减负：sessionStorage 回显 + 请求节流 + 浏览器缓存（服务端 10s）
            var STATS_CACHE_KEY = 'uf_search_stats';

            // 渲染统计数字：三项卡片可被后台「前端设置」单独隐藏，只认固定 id。
            // 不再用 .stat-item:nth-child(n) 兜底——隐藏任一项后序号会错位，会把数字写到别的卡片上。
            window.renderSearchStats = function(data) {
                if (!data) return;
                var todayEl = document.getElementById('statToday');
                if (todayEl && data.today_formatted != null) {
                    todayEl.textContent = data.today_formatted;
                }
                var totalEl = document.getElementById('statTotal');
                if (totalEl && data.total_formatted != null) {
                    totalEl.textContent = data.total_formatted;
                }
                // 搜索耗时 = pan_search_log.cost_ms 的平均值（含缓存命中），无数据时保留服务端渲染的兜底值
                var costEl = document.getElementById('statAvgCost');
                if (costEl && data.avg_cost_formatted) {
                    costEl.textContent = data.avg_cost_formatted;
                }
            };

            // 页面上是否存在统计卡片：后台隐藏统计面板（或三项全关）时整块不渲染，此时无需请求接口
            var hasStatsNodes = function() {
                return !!(document.getElementById('statToday')
                    || document.getElementById('statTotal')
                    || document.getElementById('statAvgCost'));
            };

            // 更新搜索统计数据
            // force=true：忽略节流并跳过浏览器缓存（搜索完成后调用，保证刚写入的 +1 立刻可见）
            window.updateSearchStats = function(force) {
                if (!hasStatsNodes()) return;
                var now = Date.now();
                if (!force) {
                    // 5s 内重复触发直接忽略，避免搜索完成与页面初始化各打一次接口
                    if (window.__statsFetching) return;
                    if (window.__statsLastAt && now - window.__statsLastAt < 5000) return;
                }
                window.__statsLastAt = now;
                window.__statsFetching = true;
                fetch('/api/other/get_search_stats', force ? { cache: 'reload' } : undefined)
                    .then(function(response) {
                        return response.json();
                    })
                    .then(function(res) {
                        if (res && res.code === 200 && res.data) {
                            window.renderSearchStats(res.data);
                            try {
                                sessionStorage.setItem(STATS_CACHE_KEY, JSON.stringify(res.data));
                            } catch (e) {
                            }
                        }
                    })
                    .catch(function(err) {
                    })
                    .then(function() {
                        window.__statsFetching = false;
                    });
            };

            // 页面就绪：先用上次缓存的数字回显（服务端未渲染的场景避免占位/闪烁），再异步取最新
            var initSearchStats = function() {
                // 统计面板被后台隐藏时既无元素可回显，也不该发起请求
                if (!hasStatsNodes()) return;
                try {
                    var cached = sessionStorage.getItem(STATS_CACHE_KEY);
                    if (cached) {
                        window.renderSearchStats(JSON.parse(cached));
                    }
                } catch (e) {
                }
                window.updateSearchStats();
            };
            if (document.readyState === 'loading') {
                document.addEventListener('DOMContentLoaded', initSearchStats);
            } else {
                initSearchStats();
            }

            // 更新搜索耗时显示（仅用于搜索结果栏的"本次耗时"；首页统计卡片 #statAvgCost 显示的是平均耗时，不参与计时）
            window.updateSearchDuration = function() {
                var loadTimeEl = document.getElementById('searchDuration');
                if (loadTimeEl && window.searchStartTime) {
                    var durationMs = Date.now() - window.searchStartTime;
                    var durationSec = (durationMs / 1000).toFixed(2);
                    loadTimeEl.textContent = durationSec;
                }
            };

            // 开始实时计时：记录开始时间，以 100ms-1000ms 的随机间隔刷新耗时，直到搜索完成停止。
            // 页面没有结果栏耗时元素（如首页）时不启动，避免无意义的定时器。
            window.startSearchTimer = function() {
                if (!document.getElementById('searchDuration')) {
                    window.searchStartTime = null;
                    return;
                }
                window.searchStartTime = Date.now();
                if (window.searchDurationTimer) clearTimeout(window.searchDurationTimer);
                var tick = function() {
                    window.updateSearchDuration();
                    window.searchDurationTimer = setTimeout(tick, 100 + Math.random() * 900);
                };
                tick();
            };

            // 停止计时：清除定时器并做最后一次刷新，使耗时停在最终值
            window.stopSearchTimer = function() {
                if (window.searchDurationTimer) {
                    clearTimeout(window.searchDurationTimer);
                    window.searchDurationTimer = null;
                }
                window.updateSearchDuration();
            };

            // 更新搜索结果描述中的总记录数（显示为"搜索结果共有 N 条记录"）
            window.setResultCount = function(n) {
                var countEl = document.getElementById('searchResultCount');
                if (countEl) countEl.textContent = n;
            };

            // 搜索框左侧图标：选中网盘类型时显示对应网盘图标
            // 图标名由服务端按 AggPanSearch.Models.CloudTypes 输出到标签的 data-pan-icon（如 quark / tianyi / 115），
            // 文件位于 /userfrontend/images/pan/{name}.png|svg|webp；文件缺失或无图标名时回退为默认放大镜
            var PAN_ICON_DIR = '/userfrontend/images/pan/';
            var PAN_ICON_EXTS = ['png', 'svg', 'webp'];
            var SEARCH_ICON_DEFAULT = '/userfrontend/images/search.svg';
            // 生成网盘类型小图标 HTML：与搜索框 search-icon-pan 同一套图片资源
            // （/userfrontend/images/pan/{name}.png|svg，缺失时做 png→svg→webp 回退，全部失败则隐藏）
            var buildPanTypeIconHtml = function(name) {
                if (!name) return '';
                return '<img class="search-result-tag-icon" src="' + PAN_ICON_DIR + name + '.' + PAN_ICON_EXTS[0] + '" data-pan-base="' + PAN_ICON_DIR + name + '" alt="" onerror="buildPanTypeIconFallback(this)" />';
            };
            // 类型标签图标的图片回退：png → svg → webp，全失败后隐藏
            window.buildPanTypeIconFallback = function(img) {
                if (!img) return;
                var base = img.getAttribute('data-pan-base') || '';
                var idx = (parseInt(img.getAttribute('data-pan-ext'), 10) || 0) + 1;
                if (!base || idx >= PAN_ICON_EXTS.length) { img.style.display = 'none'; return; }
                img.setAttribute('data-pan-ext', idx);
                img.src = base + '.' + PAN_ICON_EXTS[idx];
            };
            // 资源弹窗 pan-name：网盘类型图标（与搜索框 search-icon-pan 同一套图标资源）+ 网盘名称
            // 供 _Header.cshtml 资源弹窗中 v-html 渲染使用
            var DIALOG_PAN_ICON_MAP = { 0: 'quark', 1: 'aliyun', 2: 'baidu', 3: 'uc', 4: 'xunlei', 5: '123', 6: '115', 7: 'tianyi', 8: 'mobile', 9: 'magnet', 10: 'guangya', 11: 'pikpak', 12: 'ed2k', 13: 'others' };
            const dialogPanNameHtml = function(isType) {
                var num = parseInt(isType, 10);
                if (isNaN(num)) num = 0;
                var iconName = DIALOG_PAN_ICON_MAP[num] || '';
                var iconHtml = iconName ? buildPanTypeIconHtml(iconName).replace('search-result-tag-icon', 'resource-pan-icon') : '';
                var nameText = (panTypeMap.value && panTypeMap.value[num]) || '夸克网盘';
                return iconHtml + '<span class="resource-pan-name">' + nameText + '</span>';
            };
            var resetSearchPanIcon = function() {
                var box = document.getElementById('searchInputIcon');
                if (!box) return;
                box.classList.remove('has-pan');
                box.removeAttribute('data-pan');
                var img = box.querySelector('.search-icon-pan');
                if (img) {
                    img.onload = null;
                    img.onerror = null;
                    // 未选中网盘类型时，图标位恢复为默认放大镜
                    img.src = SEARCH_ICON_DEFAULT;
                }
            };
            // 从网盘类型标签上取图标名（电脑端/手机端标签都带 data-pan-icon）
            var resolvePanIconName = function(type) {
                if (!type) return '';
                var el = document.querySelector('.pan-type-tab[data-type="' + type + '"], .pan-type-mobile-btn[data-type="' + type + '"]');
                return el ? (el.getAttribute('data-pan-icon') || '') : '';
            };
            window.updateSearchPanIcon = function(panType) {
                var box = document.getElementById('searchInputIcon');
                if (!box) return;
                var img = box.querySelector('.search-icon-pan');
                var type = String(panType == null ? '' : panType).trim();
                // 未选中类型（空值，即全部）或未配置图标名时，图标位保持放大镜
                var iconName = /^\d+$/.test(type) ? resolvePanIconName(type) : '';
                if (!img || !iconName) {
                    resetSearchPanIcon();
                    return;
                }
                var extIndex = 0;
                img.onload = function() {
                    box.classList.add('has-pan');
                    box.setAttribute('data-pan', type);
                };
                img.onerror = function() {
                    extIndex++;
                    if (extIndex < PAN_ICON_EXTS.length) {
                        img.src = PAN_ICON_DIR + iconName + '.' + PAN_ICON_EXTS[extIndex];
                    } else {
                        resetSearchPanIcon();
                    }
                };
                img.alt = '';
                img.src = PAN_ICON_DIR + iconName + '.' + PAN_ICON_EXTS[extIndex];
            };

            // 切换网盘类型
            window.switchPanType = function(el) {
                // 停止当前检测
                if (window.stopLinkChecking) {
                    window.stopLinkChecking();
                }
                
                // 再次点击已选中的类型表示取消选中，回到"全部类型"
                var clickedType = el.getAttribute('data-type') || '';
                currentPanType = (clickedType === currentPanType) ? '' : clickedType;

                // 搜索框图标同步为当前网盘类型（未选中任何类型时为放大镜）
                window.updateSearchPanIcon(currentPanType);
                
                // 同步更新所有区域的按钮状态（根据data-type）
                document.querySelectorAll('.pan-type-tab').forEach(function(tab) {
                    tab.classList.remove('active');
                    if (tab.getAttribute('data-type') === currentPanType) {
                        tab.classList.add('active');
                    }
                });

                // 已有全量数据时，直接在前端按类型过滤渲染，避免重复请求
                if (keyword.value && window.__searchByTypeData) {
                    searchResults = filterMergedForDisplay(window.__searchByTypeData, currentPanType);
                    window.currentSearchData = searchResults;
                    if (searchResults.length > 0) {
                        window.setResultCount(searchResults.length);
                        renderSearchResults(searchResults);
                        setTimeout(function() { batchCheckLinksForSearchResults(); }, 300);
                    } else {
                        document.getElementById('searchResultContent').innerHTML =
                            '<div class="search-result-empty"><i class="iconfont icon-kong"></i> 该网盘类型暂无结果，可切换上方其他类型或更换关键词</div>';
                    }
                } else if (keyword.value) {
                    // 尚无全量数据时，走一次搜索（默认按全部类型拉取）
                    searchOnly();
                }
            };

            // 移动端按钮组切换线路
            window.switchPanTypeMobile = function(btn) {
                // 停止当前检测
                if (window.stopLinkChecking) {
                    window.stopLinkChecking();
                }
                
                // 获取点击的按钮类型值；再次点击已选中的类型表示取消选中，回到"全部类型"
                var clickedType = btn.getAttribute('data-type') || '';
                currentPanType = (clickedType === currentPanType) ? '' : clickedType;

                // 搜索框图标同步为当前网盘类型（未选中任何类型时为放大镜）
                window.updateSearchPanIcon(currentPanType);

                // 同步更新所有移动端按钮状态（根据data-type）
                document.querySelectorAll('.pan-type-mobile-btn').forEach(function(item) {
                    item.classList.remove('active');
                    if (item.getAttribute('data-type') === currentPanType) {
                        item.classList.add('active');
                    }
                });

                // 只要输入了关键词就重新搜索
                if (keyword.value) {
                    searchOnly();
                }
            };

            // 从缓存加载搜索结果（先短暂显示 loading，保证点击搜索时有等待反馈）
            const loadSearchResultsFromCache = (cacheData) => {
                var section = document.getElementById('searchResultSection');
                var content = document.getElementById('searchResultContent');
                var countEl = document.getElementById('searchResultCount');

                section.classList.add('show');
                // 先显示等待效果，避免缓存命中时看起来像"没反应"
                content.innerHTML = '<div class="search-result-loading"><div class="loading-wrapper"><div class="loading-pulse"></div><div class="loading-pulse"></div><div class="loading-pulse"></div><i class="iconfont icon-jiazai"></i></div><div class="loading-text">正在全网搜索</div></div>';
                window.setResultCount(0);

                // 缓存的全网搜索分组数据（merged_by_type 结构）
                var mergedByType = cacheData.data || {};
                window.__searchByTypeData = mergedByType;
                updatePanTypeTabCounts(mergedByType);
                searchResults = filterMergedForDisplay(mergedByType, currentPanType);

                // 检查是否有缓存的检测结果
                var hasCachedResults = cacheData.checkResults && Object.keys(cacheData.checkResults).length > 0;

                // 短暂延时后渲染结果，让等待效果清晰可见
                setTimeout(function() {
                    window.setResultCount(searchResults.length);
                    // 渲染搜索结果，如果有缓存的检测结果一并应用
                    renderSearchResults(searchResults, cacheData.checkResults);
                    // 启动链接检测（会跳过已缓存的检测结果，继续检测未完成的链接）
                    batchCheckLinksForSearchResults();
                }, 350);
            };

            // 依据后端返回的分组数据，更新各网盘类型 tab 上的结果数量徽标
            const updatePanTypeTabCounts = (mergedByType) => {
                var counts = {};   // is_type -> count
                if (mergedByType) {
                    Object.keys(mergedByType).forEach(function(type) {
                        var items = mergedByType[type];
                        if (!Array.isArray(items) || items.length === 0) return;
                        var it = items[0];
                        var isType = (it && it.is_type != null) ? String(it.is_type) : '';
                        counts[isType] = (counts[isType] || 0) + items.length;
                    });
                }
                document.querySelectorAll('#sourceTabs .pan-type-tab').forEach(function(tab) {
                    var dt = (tab.getAttribute('data-type') || '').trim();
                    var n = counts[dt] || 0;
                    var badge = tab.querySelector('.tab-count');
                    if (!badge) {
                        badge = document.createElement('span');
                        badge.className = 'tab-count';
                        tab.appendChild(badge);
                    }
                    badge.textContent = n;
                    badge.classList.toggle('zero', n === 0);
                });
            };

            // 依据当前选中的网盘类型，从全量分组中取出用于展示的单条列表
            const filterMergedForDisplay = (mergedByType, panType) => {
                var list = [];
                if (!mergedByType) return list;
                var pushItems = function(items) {
                    if (!Array.isArray(items)) return;
                    items.forEach(function(item) {
                        if (!item || !item.url) return;
                        list.push(Object.assign({}, item, { title: item.note || item.title || '未知资源' }));
                    });
                };
                var type = String(panType == null ? '' : panType).trim();
                Object.keys(mergedByType).forEach(function(key) {
                    var items = mergedByType[key];
                    if (!Array.isArray(items) || items.length === 0) return;
                    // 未选中任何网盘类型时展示全部类型的结果，否则只展示匹配的分组
                    if (!type || String(items[0].is_type) === type) pushItems(items);
                });
                return list;
            };

            // 统一搜索流程：searchBtn（用户点搜索）与 searchOnly（切换线路/网盘类型）共用
            // opts.emptyTipFromConfig：空结果使用后台配置的提示图/文案
            // opts.countStats：成功后更新搜索统计（仅用户主动搜索计入）
            // opts.trafficGuard：结果展示完成后触发公众号引流弹窗（仅用户主动搜索）
            // opts.updatePanIcon：搜索前刷新搜索框左侧的网盘图标
            const runSearch = async (opts) => {
                opts = opts || {};
                if (!keyword.value) {
                    return showMessage('请输入你要搜索的内容~', 'error');
                }

                // 停止上一轮的链接检测
                if (window.stopLinkChecking) {
                    window.stopLinkChecking();
                }

                // 记录搜索开始时间并启动实时计时；重置本轮结果数据
                window.startSearchTimer();
                window.currentSearchData = [];

                if (opts.updatePanIcon) {
                    // 未选中任何网盘类型时保持空值，搜索后展示全部类型的结果
                    window.updateSearchPanIcon(currentPanType);
                }

                var section = document.getElementById('searchResultSection');
                var content = document.getElementById('searchResultContent');
                if (section) section.classList.add('show');
                if (content) content.innerHTML = '<div class="search-result-loading"><div class="loading-wrapper"><div class="loading-pulse"></div><div class="loading-pulse"></div><div class="loading-pulse"></div><i class="iconfont icon-jiazai"></i></div><div class="loading-text">正在全网搜索</div></div>';
                window.setResultCount(0);
                var durEl = document.getElementById('searchDuration');
                if (durEl) durEl.textContent = '0.00';

                // 始终按"全部"类型请求：一次性拿到各网盘类型的完整分组，用于 tab 计数与前端过滤
                try {
                    var response = await axios.get('/api/other/web_search', { params: { title: keyword.value } });
                    var data = response.data && response.data.data;
                    var mergedByType = (data && data.merged_by_type) || {};
                    // 保存全量分组，供切换类型时前端过滤与 tab 计数使用
                    window.__searchByTypeData = mergedByType;
                    updatePanTypeTabCounts(mergedByType);
                    searchResults = filterMergedForDisplay(mergedByType, currentPanType);

                    if (searchResults.length > 0) {
                        window.setResultCount(searchResults.length);
                        // 全量渲染（已将当前类型分组展平为单条记录）
                        renderSearchResults(searchResults);
                        // 保存全量分组到缓存（按关键词 + 全部类型维度）
                        setSearchCache(keyword.value, '', mergedByType);
                        // 搜索结束后检测链接有效性
                        setTimeout(function() {
                            batchCheckLinksForSearchResults();
                        }, 500);
                    } else if (data && data.blocked) {
                        var blockedIcon = opts.emptyTipFromConfig ? '' : '<i class="iconfont icon-kong"></i> ';
                        content.innerHTML = '<div class="search-result-empty">' + blockedIcon + '该关键词已按合规要求屏蔽</div>';
                    } else if (opts.emptyTipFromConfig) {
                        var emptyTip = (window.__UF__ && window.__UF__.searchTips) ? window.__UF__.searchTips : '未找到相关资源';
                        var emptyImg = (window.__UF__ && window.__UF__.searchBg) ? '<img src="' + window.__UF__.searchBg + '" style="width:200px;max-width:70%;margin:0 auto 10px;display:block;" />' : '';
                        content.innerHTML = '<div class="search-result-empty">' + emptyImg + emptyTip + '</div>';
                    } else {
                        content.innerHTML = '<div class="search-result-empty"><i class="iconfont icon-kong"></i> 暂无搜索结果，请尝试其他关键词</div>';
                    }

                    // force：跳过浏览器缓存，刚写入的那次搜索立刻计入
                    if (opts.countStats) updateSearchStats(true);
                    // 搜索完成后停止实时计时（耗时停在最终值）
                    window.stopSearchTimer();

                    // 公众号引流：放在结果渲染之后执行，不阻塞搜索，用户关闭/激活弹窗即可继续浏览结果
                    if (opts.trafficGuard && typeof window.wxTrafficGuard === 'function') {
                        window.wxTrafficGuard(1);
                    }
                } catch (error) {
                    if (content) content.innerHTML = '<div class="search-result-empty">搜索出错，请稍后重试</div>';
                    if (opts.countStats) updateSearchStats(true);
                    window.stopSearchTimer();
                }
            };

            // 仅搜索（用于切换线路/网盘类型）：不弹引流、不计入统计、空结果用站内固定文案
            const searchOnly = () => runSearch({});

            // 搜索按钮点击事件 - AggPanSearch 全网搜（JSON 一次性返回）
            const searchBtn = () => runSearch({
                updatePanIcon: true,
                emptyTipFromConfig: true,
                countStats: true,
                trafficGuard: true
            });
            
            // 渲染单条搜索结果项
            window.renderSearchResultItem = function(item, index) {
                // 类型名称与站点配置（pan_type_map）保持一致，避免前后台中文名不一致
                var panTypeMap = window.__UF__.panTypeMap || {};
                var title = item.title || item.note || '未知资源';
                var desc = item.desc || item.description || '';
                // 将 is_type 转换为数字来匹配 panTypeMap 的键
                var isTypeNum = parseInt(item.is_type, 10);
                var typeLabel = panTypeMap[isTypeNum] || item.pantype || '网盘资源';
                // 网盘类型→图标文件名映射（取自 CloudTypes 常量，与搜索框 search-icon-pan 同一套图标）：
                // 文件位于 /userfrontend/images/pan/{name}.png|svg，未收录的类型不显示图标
                var panIconMap = { 0: 'quark', 1: 'aliyun', 2: 'baidu', 3: 'uc', 4: 'xunlei', 5: '123', 6: '115', 7: 'tianyi', 8: 'mobile', 9: 'magnet', 10: 'guangya', 11: 'pikpak', 12: 'ed2k', 13: 'others' };
                var typeIconHtml = buildPanTypeIconHtml(panIconMap[isTypeNum] || '');
                var rawTime = item.datetime || item.time || item.date || item.create_time || item.created_at || '';
                var timeStr = '';
                if (rawTime) {
                    timeStr = rawTime.substring(0, 19).replace('T', ' ');
                    if (timeStr.indexOf('0000-') === 0 || timeStr.indexOf('0001-') === 0) {
                        var now = new Date();
                        timeStr = now.getFullYear() + '-' + 
                                 String(now.getMonth() + 1).padStart(2, '0') + '-' + 
                                 String(now.getDate()).padStart(2, '0') + ' ' +
                                 String(now.getHours()).padStart(2, '0') + ':' +
                                 String(now.getMinutes()).padStart(2, '0') + ':' +
                                 String(now.getSeconds()).padStart(2, '0');
                    }
                }
                // 判断是否本地资源
                var isLocalResource = item.source === '本地资源';
                var localTag = isLocalResource ? '<span class="search-result-tag local" style="background: linear-gradient(135deg, #ff6b6b, #ff8e8e); color: #fff;">本地资源</span>' : '';
                
                // 链接有效性检测状态：状态存在数据层（item._checkState），这里只按状态出 HTML。
                // 这样翻页/切换网盘类型重建 DOM 后状态不会回退成"检测中"，也不会重复发起请求。
                // 取值：''=待检测 | 'valid' | 'invalid' | 'error'
                var checkState = item._checkState || '';
                var checkTextMap = { valid: '有效', invalid: '失效', error: '检测失败' };
                // error 状态需要把后端 message 折叠成语义化文案（与 updateLinkStatus 一致），重建 DOM 后不退化
                if (checkState === 'error') checkTextMap.error = describeCheckStatus(item._checkResult) || '检测失败';
                // 封禁/审核/屏蔽类失效：文案显示"链接失效"，且不隐藏整条（_keepVisible 由数据层标记，缓存回填后依然生效）
                if (checkState === 'invalid' && item._keepVisible) checkTextMap.invalid = '链接失效';
                var checkStatusHtml = '<span class="search-result-tag link-check-status' + (checkState ? ' ' + checkState : '') + '" data-url="' + (item.url || '') + '" data-index="' + index + '" style="background: #f5f5f5; color: #666; font-size: 11px; padding: 2px 8px;">';
                if (checkState) {
                    // 已有结论：不再渲染转圈图标（CSS 里 valid/invalid/error 用 ::before 自带 ✓ / ✗ / !）
                    checkStatusHtml += '<span class="check-text">' + (checkTextMap[checkState] || '') + '</span>';
                } else {
                    checkStatusHtml += '<span class="check-icon" style="display: inline-block; width: 10px; height: 10px; margin-right: 4px; border: 2px solid #999; border-top-color: transparent; border-radius: 50%; animation: linkCheckRotate 1s linear infinite;"></span>';
                    checkStatusHtml += '<span class="check-text">检测中</span>';
                }
                checkStatusHtml += '</span>';

                // 失效链接保持"检测完就隐藏整条"的既有表现；改成看数据层状态，
                // 重建 DOM 时不会再闪回来，也不会在翻页时把已隐藏的失效项重新显示一次。
                // 例外：封禁/审核/屏蔽类失效(_keepVisible)显示"链接失效"但不隐藏整条
                var itemStyle = 'animation: fadeIn 0.3s ease;' + (checkState === 'invalid' && !item._keepVisible ? ' display: none;' : '');
                var html = '<div class="search-result-item" data-index="' + index + '" style="' + itemStyle + '">';
                html += '<div class="search-result-info">';
                // 名称行：资源名称 + 链接检测状态(link-check-status)同行
                html += '<div class="search-result-name-row">';
                html += '<span class="search-result-name">' + title + '</span>';
                html += checkStatusHtml;
                html += '</div>';
                // 标签行：类型 / 本地资源 / 时间 / 来源 等标签独立一行展示
                html += '<div class="search-result-tags-row">';
                html += '<span class="search-result-tag type">' + typeIconHtml + typeLabel + '</span>';
                html += localTag;
                if (timeStr) {
                    html += '<span class="search-result-tag source">' + timeStr + '</span>';
                }
                // 数据来源字段（tg:频道 / plugin:插件 / 本地资源），显示完整来源值，保留 tg:/plugin: 前缀
                // 是否显示受后台"搜索设置 → is_show_source"控制：1 显示，0/未配置隐藏
                var showSource = (window.__UF__ && window.__UF__.isShowSource === 1);
                var sourceLabel = (item.source || '').trim();
                if (sourceLabel === '本地资源') {
                    sourceLabel = '';
                }
                if (sourceLabel && showSource) {
                    html += '<span class="search-result-tag source" title="' + (item.source || '') + '">' + sourceLabel + '</span>';
                }
                html += '</div>';
                if (desc) {
                    html += '<div class="search-result-desc">' + desc + '</div>';
                }
                html += '</div>';
                html += '<div class="search-result-actions">';
                // 「查看资源」直接绑定引流拦截入口：后台选「查看详情页资源链接强制弹窗」时，点它才弹公众号弹窗
                html += '<span class="search-result-btn get" onclick="event.stopPropagation(); getResource(event, ' + index + ')"><i class="iconfont icon-xiazai"></i> 查看资源</span>';
                html += '</div>';
                html += '</div>';
                return html;
            };
            
            // 添加淡入动画样式
            if (!document.getElementById('searchResultAnimations')) {
                var style = document.createElement('style');
                style.id = 'searchResultAnimations';
                style.textContent = '@keyframes fadeIn { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: translateY(0); } }';
                document.head.appendChild(style);
            }
            
            // 添加链接检测动画样式
            if (!document.getElementById('linkCheckAnimations')) {
                var linkCheckStyle = document.createElement('style');
                linkCheckStyle.id = 'linkCheckAnimations';
                linkCheckStyle.textContent = `
                    @keyframes linkCheckRotate {
                        from { transform: rotate(0deg); }
                        to { transform: rotate(360deg); }
                    }
                    .link-check-status {
                        transition: all 0.3s ease;
                    }
                    .link-check-status.valid {
                        background: #f6ffed !important;
                        color: #52c41a !important;
                        border: 1px solid #b7eb8f;
                    }
                    .link-check-status.valid .check-icon {
                        animation: none !important;
                        border: none !important;
                        width: auto !important;
                        height: auto !important;
                    }
                    .link-check-status.valid .check-text::before {
                        content: '✓ ';
                    }
                    .link-check-status.invalid {
                        background: #fff2f0 !important;
                        color: #ff4d4f !important;
                        border: 1px solid #ffccc7;
                    }
                    .link-check-status.invalid .check-icon {
                        animation: none !important;
                        border: none !important;
                        width: auto !important;
                        height: auto !important;
                    }
                    .link-check-status.invalid .check-text::before {
                        content: '✗ ';
                    }
                    .link-check-status.error {
                        background: #fffbe6 !important;
                        color: #faad14 !important;
                        border: 1px solid #ffe58f;
                    }
                    .link-check-status.error .check-icon {
                        animation: none !important;
                        border: none !important;
                        width: auto !important;
                        height: auto !important;
                    }
                    .link-check-status.error .check-text::before {
                        content: '! ';
                    }
                `;
                document.head.appendChild(linkCheckStyle);
            }
            
            // 渲染搜索结果（本地分页渲染）
            window.renderSearchResults = function(data, cachedCheckResults) {
                var content = document.getElementById('searchResultContent');
                // 保持全量数据在 currentSearchData，供详情/文件列表通过全局下标正确取数
                window.currentSearchData = data || [];
                
                // 新的数据（新搜索/切换类型）重置到第 1 页；同一数据对象再次调用（翻页）保持当前页
                if (window.__searchPageData !== data) {
                    window.__searchPageData = data;
                    // 切换类型/新搜索时重置页码与每页条数
                    window.searchPageIndex = 1;
                    var savedSize = parseInt(localStorage.getItem('search_page_size'), 10);
                    window.searchPageSize = (savedSize && savedSize > 0) ? savedSize : 15;
                }
                
                var total = (window.currentSearchData && window.currentSearchData.length) || 0;
                var pageSize = window.searchPageSize || 15;
                var totalPages = Math.max(1, Math.ceil(total / pageSize));
                if (window.searchPageIndex > totalPages) window.searchPageIndex = totalPages;
                if (window.searchPageIndex < 1) window.searchPageIndex = 1;
                var pageIndex = window.searchPageIndex;
                var start = (pageIndex - 1) * pageSize;
                var end = Math.min(total, start + pageSize);
                
                // 渲染前先把检测状态回填到数据层：
                // 1) 优先 URL 级缓存（跨搜索、跨网盘类型、跨会话复用，命中就完全不再发请求）
                // 2) 兼容旧的 cachedCheckResults 入参（按关键词缓存的那份），仅作补充
                var hasHiddenItem = false;
                if (total > 0) {
                    for (var i = start; i < end; i++) {
                        var hydrated = hydrateCheckState(window.currentSearchData[i], cachedCheckResults);
                        // 封禁/审核/屏蔽类失效(_keepVisible)保留展示，不算隐藏项
                        if (hydrated && hydrated._checkState === 'invalid' && !hydrated._keepVisible) hasHiddenItem = true;
                    }
                }

                var html = '<div class="search-result-list">';
                if (total > 0) {
                    for (var i = start; i < end; i++) {
                        // 使用全局下标 i 渲染，保证与 currentSearchData 的定位一致
                        html += window.renderSearchResultItem(window.currentSearchData[i], i);
                    }
                }
                html += '</div>';
                
                // 分页条
                if (totalPages > 1) {
                    html += '<div class="search-pagination">';
                    html += '<button class="page-first" onclick="gotoSearchPage(1)" ' + (pageIndex <= 1 ? 'disabled' : '') + '>« 首页</button>';
                    html += '<button onclick="gotoSearchPage(' + (pageIndex - 1) + ')" ' + (pageIndex <= 1 ? 'disabled' : '') + '>‹ 上一页</button>';
                    
                    var pageButtons = buildPageNumbers(pageIndex, totalPages);
                    for (var b = 0; b < pageButtons.length; b++) {
                        var pb = pageButtons[b];
                        if (pb === '...') {
                            html += '<span class="page-ellipsis">…</span>';
                        } else {
                            html += '<button class="' + (pb === pageIndex ? 'active' : '') + '" onclick="gotoSearchPage(' + pb + ')">' + pb + '</button>';
                        }
                    }
                    
                    html += '<button onclick="gotoSearchPage(' + (pageIndex + 1) + ')" ' + (pageIndex >= totalPages ? 'disabled' : '') + '>下一页 ›</button>';
                    html += '<button class="page-last" onclick="gotoSearchPage(' + totalPages + ')" ' + (pageIndex >= totalPages ? 'disabled' : '') + '>末页 »</button>';
                    html += '<span class="page-info">' + pageIndex + ' / ' + totalPages + ' 页</span>';
                    html += '<select class="page-size" onchange="changeSearchPageSize(this.value)">';
                    [10, 15, 20, 30, 50].forEach(function(size) {
                        html += '<option value="' + size + '" ' + (size === pageSize ? 'selected' : '') + '>' + size + '条/页</option>';
                    });
                    html += '</select>';
                    html += '</div>';
                }
                
                content.innerHTML = html;

                // 命中缓存的失效项在渲染时已隐藏，这里同步一下"共有 N 条记录"的展示，与逐条检测后的表现一致
                if (hasHiddenItem) updateSearchResultCount();
            };

            // 生成分页数字按钮（带省略号折叠，顺序正确）
            function buildPageNumbers(current, total) {
                var pages = [];
                var push = function(p) {
                    if (pages.length > 0 && p === pages[pages.length - 1]) return; // 去重相邻
                    pages.push(p);
                };
                if (current > 3) {
                    push(1);
                    push('...');
                }
                for (var p = Math.max(1, current - 1); p <= Math.min(total, current + 1); p++) {
                    push(p);
                }
                if (current < total - 2) {
                    push('...');
                    push(total);
                }
                return pages;
            }

            // 跳转到指定页（本地分页）
            window.gotoSearchPage = function(page) {
                var data = window.__searchPageData;
                if (!data || !data.length) return;
                var totalPages = Math.max(1, Math.ceil(data.length / (window.searchPageSize || 15)));
                if (page < 1) page = 1;
                if (page > totalPages) page = totalPages;
                window.searchPageIndex = page;
                
                // 停止当前链接检测，避免旧页检测串到新页
                if (window.stopLinkChecking) window.stopLinkChecking();
                
                window.renderSearchResults(data);
                // 检测当前页链接有效性
                setTimeout(function() {
                    batchCheckLinksForSearchResults();
                }, 300);
                
                // 滚动回结果区顶部，便于浏览
                var section = document.getElementById('searchResultSection');
                if (section) section.scrollIntoView({ behavior: 'smooth', block: 'start' });
            };

            // 切换每页条数
            window.changeSearchPageSize = function(size) {
                size = parseInt(size, 10) || 15;
                window.searchPageSize = size;
                try { localStorage.setItem('search_page_size', String(size)); } catch (e) {}
                window.searchPageIndex = 1;
                var data = window.__searchPageData;
                if (data && data.length) {
                    window.renderSearchResults(data);
                    setTimeout(function() { batchCheckLinksForSearchResults(); }, 300);
                }
            };
            
            // ==================== 链接有效性检测功能 ====================
            
            // 检测状态标志
            window.isCheckingLinks = false;
            window.shouldStopChecking = false;
            
            // 停止当前检测
            window.stopLinkChecking = function() {
                if (window.isCheckingLinks) {
                    window.shouldStopChecking = true;
                }
            };
            
            // 把检测结果写到数据层（item._checkState / item._checkResult）。
            // 渲染函数只认这两个字段，所以 DOM 被重建后状态不会丢、更不会重复请求。
            // result 兼容两种来源：实时接口结果 { valid, error } 与浏览器缓存记录 { v, e, t }。
            function applyCheckResultToItem(item, result) {
                if (!item) return;
                var valid = result.valid;
                var errorMsg = result.error;
                if (valid === undefined && typeof result.v !== 'undefined') {
                    valid = result.v;
                    if (errorMsg === undefined) errorMsg = result.e;
                }
                if (valid === true) {
                    item._checkState = 'valid';
                    item._keepVisible = false;
                } else if (valid === false) {
                    item._checkState = 'invalid';
                    // 封禁/审核/屏蔽类失效：显示"链接失效"但保留整条记录（不隐藏）
                    item._keepVisible = isKeepVisibleInvalid(errorMsg);
                } else {
                    item._checkState = 'error';
                    item._keepVisible = false;
                }
                item._checkResult = { valid: valid, error: errorMsg };
            }

            // 封禁/审核/屏蔽等平台限制类失效：确证链接不可用，但保留整条记录供用户查看
            function isKeepVisibleInvalid(msg) {
                if (!msg) return false;
                var s = String(msg).toLowerCase();
                return s.indexOf('封禁') >= 0 || s.indexOf('审核') >= 0 || s.indexOf('屏蔽') >= 0 ||
                    s.indexOf('限制查看') >= 0 || s.indexOf('下架') >= 0;
            }

            // 把后端 message / 错误信息折叠成简短语义文案，不再一律显示"检测失败"
            function describeCheckStatus(result) {
                if (!result) return '检测失败';
                if (result.valid === true) return '有效';
                if (result.valid === false) return '失效';
                var msg = String(result.error || '').toLowerCase();
                if (msg.indexOf('暂不支持') >= 0 || msg.indexOf('暂未支持') >= 0 || msg.indexOf('unsupported') >= 0) return '暂不支持';
                if (msg.indexOf('解析失败') >= 0 || msg.indexOf('failed to parse') >= 0 || msg.indexOf('无响应') >= 0) return '解析失败';
                if (msg.indexOf('受限') >= 0 || msg.indexOf('限流') >= 0 || msg.indexOf('too many') >= 0 || msg.indexOf('frequency') >= 0 || msg.indexOf('throttl') >= 0) return '请求受限';
                if (msg.indexOf('超时') >= 0 || msg.indexOf('timeout') >= 0 || msg.indexOf('timed out') >= 0) return '请求超时';
                if (msg.indexOf('取消') >= 0 || msg.indexOf('canceled') >= 0 || msg.indexOf('cancelled') >= 0) return '检测取消';
                return '检测失败';
            }

            // 渲染前用缓存回填检测状态；已有状态的不覆盖
            function hydrateCheckState(item, legacyResults) {
                if (!item) return null;
                if (item._checkState) return item;
                var rec = getCachedLinkCheck(item.url);
                if (!rec && legacyResults && item.url && legacyResults[item.url]) {
                    rec = legacyResults[item.url];
                }
                if (rec) applyCheckResultToItem(item, rec);
                return item;
            }

            // 按索引 + URL 定位状态元素。检测是异步的，结果回来时 DOM 可能已被翻页/切换类型重建，
            // 直接改收集时抓到的旧引用会把状态写到已脱离文档的节点上，新页面反而显示不出结果。
            function resolveStatusElement(index, url) {
                var content = document.getElementById('searchResultContent');
                if (!content) return null;
                var node = content.querySelector('.search-result-item[data-index="' + index + '"] .link-check-status');
                if (node && node.getAttribute('data-url') === url) return node;
                var all = content.querySelectorAll('.link-check-status');
                for (var i = 0; i < all.length; i++) {
                    if (all[i].getAttribute('data-url') === url) return all[i];
                }
                return null;
            }

            // 单条结果统一落库：数据层状态 + URL 级缓存 + DOM 展示，三者一起更新，避免状态不一致
            function applyCheckResult(url, index, item, result) {
                applyCheckResultToItem(item, result);
                setCachedLinkCheck(url, result);
                var el = resolveStatusElement(index, url);
                if (el) updateLinkStatus(el, result);
            }

            // 收集当前页尚未得出结论的条目（以数据层状态为准，同一 URL 只检测一次）
            function collectPendingCheckItems() {
                var content = document.getElementById('searchResultContent');
                if (!content) return [];
                var data = window.currentSearchData || [];
                var seen = {};
                var list = [];
                content.querySelectorAll('.search-result-item').forEach(function (node) {
                    var index = parseInt(node.getAttribute('data-index'), 10);
                    var item = data[index];
                    if (!item || !item.url) return;
                    if (seen[item.url]) return;
                    seen[item.url] = true;
                    // valid/invalid：命中即跳过，这是"翻页 0 请求"的关键
                    if (item._checkState === 'valid' || item._checkState === 'invalid') return;
                    // error：允许自动重试，但受门控——缓存中最近 2 分钟刚失败过的不重复轰炸
                    if (item._checkState === 'error') {
                        var cached = getCachedLinkCheck(item.url);
                        if (cached && cached.v !== true && cached.v !== false && cached.t && (Date.now() - cached.t) < LINK_CHECK_ERROR_RETRY_GATE) return;
                    }
                    list.push({ url: item.url, index: index, item: item });
                });
                return list;
            }

            // 逐条检测搜索结果的链接
            window.batchCheckLinksForSearchResults = async function() {
                // 如果正在检测，先停止之前的
                if (window.isCheckingLinks) {
                    window.stopLinkChecking();
                    // 等待一小段时间让之前的检测停止
                    await new Promise(resolve => setTimeout(resolve, 200));
                }
                
                // 重置停止标志
                window.shouldStopChecking = false;

                var checkItems = collectPendingCheckItems();
                if (checkItems.length === 0) return;

                // 设置检测标志
                window.isCheckingLinks = true;

                try {
                    // 逐条检测
                    for (var i = 0; i < checkItems.length; i++) {
                        // 检查是否需要停止
                        if (window.shouldStopChecking) {
                            break;
                        }

                        // 检查是否还在当前页面（防止页面已切换）
                        var currentContent = document.getElementById('searchResultContent');
                        if (!currentContent) {
                            break;
                        }

                        var checkItem = checkItems[i];

                        // 检测单条链接
                        await checkSingleLink(checkItem.url, checkItem.index, checkItem.item);

                        // 添加小延迟，避免请求过快
                        await new Promise(resolve => setTimeout(resolve, 100));
                    }
                } finally {
                    // 无论成功失败，都重置标志
                    window.isCheckingLinks = false;
                    window.shouldStopChecking = false;
                    // 本轮结束后立刻落盘一次，避免用户马上关页面丢掉结果
                    flushLinkCheckCache();
                }
            };
            
            // 解密链接
            async function decryptUrl(encryptedUrl) {
                try {
                    var response = await axios.post('/api/other/save_url', {
                        url: encodeURIComponent(encryptedUrl),
                        title: ''
                    });
                    if (response.data.code == 200 && response.data.data && response.data.data.url) {
                        return response.data.data.url;
                    }
                    return null;
                } catch (error) {
                    return null;
                }
            }
            
            // 判断链接是否需要解密
            function needsDecryption(url) {
                if (!url) return false;
                if (url.startsWith('http') || url.startsWith('magnet:')) return false;
                return true;
            }
            
            // 检测单条链接（缓存按原始 url 存，解密只影响请求目标，不影响缓存键）
            async function checkSingleLink(url, index, item) {
                try {
                    var checkUrl = url;
                    
                    // 检查链接是否需要解密
                    if (needsDecryption(url)) {
                        var decryptedUrl = await decryptUrl(url);
                        if (decryptedUrl) {
                            checkUrl = decryptedUrl;
                        } else {
                            applyCheckResult(url, index, item, { valid: null, error: '链接解密失败' });
                            return;
                        }
                    }

                    var response = await fetch('/api/pancheck', {
                        method: 'POST',
                        headers: {
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ links: [checkUrl] })
                    });

                    if (!response.ok) {
                        throw new Error('API 请求失败: ' + response.status);
                    }

                    var data = await response.json();

                    if (data.success && data.results && data.results.length > 0) {
                        var result = data.results[0];
                        applyCheckResult(url, index, item, { valid: result.valid, error: result.error || result.message });
                    } else {
                        applyCheckResult(url, index, item, { valid: null, error: '返回数据错误' });
                    }
                } catch (error) {
                    applyCheckResult(url, index, item, { valid: null, error: '检测失败' });
                }
            }
            
            // 更新链接状态显示
            function updateLinkStatus(element, result) {
                if (!element) {
                    return;
                }

                var textEl = element.querySelector('.check-text');
                if (!textEl) {
                    return;
                }
                
                // 清除所有状态类名
                element.classList.remove('valid', 'invalid', 'error');
                
                if (result.valid === true) {
                    element.classList.add('valid');
                    textEl.textContent = '有效';
                } else if (result.valid === false) {
                    element.classList.add('invalid');
                    // 封禁/审核/屏蔽类失效：显示"链接失效"并保留整条；普通失效才隐藏
                    var keepVisible = isKeepVisibleInvalid(result.error);
                    textEl.textContent = keepVisible ? '链接失效' : '失效';
                    if (!keepVisible) {
                        // 隐藏整条搜索记录
                        var searchItem = element.closest('.search-result-item');
                        if (searchItem) {
                            searchItem.style.display = 'none';
                            // 更新搜索结果数量
                            updateSearchResultCount();
                        }
                    }
                } else {
                    element.classList.add('error');
                    // 语义化文案：区分暂不支持 / 解析失败 / 请求受限 / 超时等，而非一律"检测失败"
                    textEl.textContent = describeCheckStatus(result);
                    element.style.cursor = 'pointer';
                    element.title = '点击重新检测';
                }
            }

            // 检测失败的标签点击后可重新检测（事件委托，兼容动态重建的 DOM）
            document.addEventListener('click', function(e) {
                var el = e.target && e.target.closest ? e.target.closest('.link-check-status.error') : null;
                if (!el) return;
                e.stopPropagation();
                var url = el.getAttribute('data-url');
                if (!url) return;

                // 重置为该条链接"检测中"状态
                el.classList.remove('error', 'valid', 'invalid');
                var textEl = el.querySelector('.check-text');
                if (textEl) textEl.textContent = '检测中';
                // 补回转圈图标（error 状态下图标被 CSS 隐藏，需要重建）
                var iconEl = el.querySelector('.check-icon');
                if (!iconEl && textEl) {
                    iconEl = document.createElement('span');
                    iconEl.className = 'check-icon';
                    iconEl.style.cssText = 'display: inline-block; width: 10px; height: 10px; margin-right: 4px; border: 2px solid #999; border-top-color: transparent; border-radius: 50%; animation: linkCheckRotate 1s linear infinite;';
                    el.insertBefore(iconEl, textEl);
                }

                // 清空数据层状态，确保重新走网络检测（而不是复用缓存结果）
                var index = parseInt(el.getAttribute('data-index'), 10);
                var item = (window.currentSearchData || [])[index];
                if (item) {
                    delete item._checkState;
                }
                checkSingleLink(url, index, item);
            });
            
            // 更新搜索结果数量显示
            function updateSearchResultCount() {
                var countEl = document.getElementById('searchResultCount');
                if (!countEl) return;
                
                var content = document.getElementById('searchResultContent');
                if (!content) return;
                
                // 获取所有搜索结果项
                var allItems = content.querySelectorAll('.search-result-item');
                var visibleItems = content.querySelectorAll('.search-result-item:not([style*="display: none"])');
                
                var totalCount = allItems.length;
                var visibleCount = visibleItems.length;
                var hiddenCount = totalCount - visibleCount;
                
                // 链接检测失效后，将展示的总记录数更新为当前可见条数
                window.setResultCount(visibleCount);
            }
            
            // 获取资源 - 先走公众号引流拦截，激活后再打开资源详情
            const getResource = function(event, index) {
                if (event && event.stopPropagation) {
                    event.stopPropagation();
                }
                // 公众号引流：后台开启「查看详情页资源链接强制弹窗」时，先弹窗关注公众号，激活后再展示资源
                if (typeof window.wxTrafficGuard === 'function') {
                    window.wxTrafficGuard(2).then(function (allow) {
                        if (allow) openResource(index);
                    });
                    return;
                }
                openResource(index);
            };

            // 打开资源详情弹窗（二维码 / 链接）
            const openResource = function(index) {
                var item = window.currentSearchData[index];
                if (!item) return;
                
                // 增加浏览量（如果是本地资源且有id）
                if (item.id) {
                    axios.post('/api/search/incrementViews', {
                        id: item.id
                    }).catch(function(err) {
                        // 静默处理错误，不影响用户体验
                        console.log('浏览量增加失败:', err);
                    });
                }
                
                dialogLoading.value = true;
                dialogUrl.value = true;
                dialogItem.value = {};
                
                var rawUrl = item.url || '';
                
                if (rawUrl.startsWith('http') || rawUrl.startsWith('magnet:')) {
                    item.showUrl = rawUrl;
                    showResourceDialog(item);
                } else {
                    axios.post('/api/other/save_url', {
                        url: encodeURIComponent(rawUrl),
                        title: item.title
                    }).then(function(res) {
                        if (res.data.code == 200) {
                            item.url = res.data.data.url;
                            item.showUrl = res.data.data.url;
                        } else {
                            item.showUrl = '';
                            item.message = res.data.message || '获取失败';
                        }
                        showResourceDialog(item);
                    }).catch(function(err) {
                        item.showUrl = '';
                        item.message = '网络错误，请重试';
                        showResourceDialog(item);
                    });
                }
            };
            
            // 绑定到window，供全局调用
            window.getResource = getResource;

            // 全局搜索方法 - 供首页热播榜点击使用
            window.searchByKeyword = function(kw) {
                // 设置关键词
                keyword.value = kw;
                // 先滚动到导览栏位置
                var topNav = document.querySelector('.top-nav');
                if (topNav) {
                    topNav.scrollIntoView({ behavior: 'smooth', block: 'start' });
                } else {
                    window.scrollTo(0, 0);
                }
                // 使用 setTimeout 确保滚动完成后再搜索
                setTimeout(function() {
                    searchBtn();
                }, 300);
            };

            // 使用迅雷JS-SDK下载磁力链接
            window.downloadWithThunder = function(url, title) {
                if (typeof thunderLink === 'undefined') {
                    showMessage('迅雷SDK加载失败，请刷新页面重试', 'error');
                    return;
                }
                
                try {
                    thunderLink.newTask({
                        taskGroupName: title || '磁力下载',
                        tasks: [{
                            url: url,
                            name: title || '未知文件'
                        }]
                    });
                } catch (error) {
                    showMessage('迅雷下载调用失败，请确保已安装迅雷客户端', 'error');
                }
            };
            
            // 显示资源弹窗和二维码
            const showResourceDialog = function(item) {
                dialogLoading.value = false;
                
                if (item.showUrl && item.showUrl.startsWith('magnet:')) {
                    item.thunderUrl = 'thunder://' + btoa('AA' + item.showUrl + 'ZZ');
                }
                
                dialogItem.value = item;
                
                if (item.showUrl) {
                    // 使用 nextTick 确保 DOM 更新后再生成二维码
                    Vue.nextTick(function() {
                        setTimeout(function() {
                            var qrcodeEl = document.getElementById('qrcode');
                            if (qrcodeEl) {
                                qrcodeEl.innerHTML = '';
                                var canvas = qrcanvas.qrcanvas({
                                    data: item.showUrl,
                                    size: 120
                                });
                                qrcodeEl.appendChild(canvas);
                            }
                        }, 100);
                    });
                }
            };
            
            // 显示详情
            const showDetail = function(index) {
                var item = window.currentSearchData[index];
                if (!item) return;
                showMessage(item.title || '暂无详情', 'info');
            };
            
            // 关闭搜索结果
            window.closeSearchResult = function() {
                document.getElementById('searchResultSection').classList.remove('show');
            };

            // 保存按钮点击事件
            const saveBtn = async () => {
                if (!content.value) {
                    return showMessage('请输入你想看的资源信息~', 'error');
                }
                if (load.value) return;
    
                load.value = true;
                try {
                    const response = await axios.post('/api/tool/feedback', { content: content.value });
                    showMessage(response.data.message, response.data.code === 200 ? 'success' : 'error');
                    if (response.data.code === 200) {
                        layerVisible.value = false;
                        content.value = '';
                    }
                } finally {
                    load.value = false;
                }
            };
            
            const setnum = (num) => (num / 10000).toFixed(2) + 'W';
            
            const goLink = (event,id) => {
                event.preventDefault();
                window.location.href = `/d/${id}.html`;
            }
            
            const changeBtn = (e) => {
                const category_id = (window.__UF__.categoryId||``);
                if(category_id){
                    window.location.href = `/s/${keyword.value}-${e}-${category_id}.html`;
                }else{
                    window.location.href = `/s/${keyword.value}-${e}.html`;
                }
            };
            
            const copyText = async(event,title,url,code) => {
                event.preventDefault();
                var text = '标题：'+title+'\n链接：'+url
                if (code) text += `\n提取码：${code}`;
                text += `\n由【${window.__UF__.appName}${window.location.hostname}】提供网盘分享链接`;
                
                
                try {
                    // 优先使用 navigator.clipboard
                    await navigator.clipboard.writeText(text);
                    showMessage('复制成功', 'success');
                } catch (err) {
                    // 如果 navigator.clipboard 失败，使用 document.execCommand 作为回退
                    const textArea = document.createElement('textarea');
                    textArea.value = text;
                    textArea.style.position = 'fixed';  // 避免滚动
                    textArea.style.opacity = 0;
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
            
                    try {
                        const successful = document.execCommand('copy');
                        if (successful) {
                            showMessage('复制成功', 'success');
                        } else {
                            throw new Error('复制失败');
                        }
                    } catch (err) {
                        showMessage('复制失败，请手动复制', 'error');
                    }
            
                    document.body.removeChild(textArea);
                }
            }

            // 复制链接到剪贴板（弹窗使用）
            const copyToClipboard = async(text) => {
                try {
                    await navigator.clipboard.writeText(text);
                    showMessage('复制成功', 'success');
                } catch (err) {
                    const textArea = document.createElement('textarea');
                    textArea.value = text;
                    textArea.style.position = 'fixed';
                    textArea.style.opacity = 0;
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
            
                    try {
                        const successful = document.execCommand('copy');
                        if (successful) {
                            showMessage('复制成功', 'success');
                        } else {
                            throw new Error('复制失败');
                        }
                    } catch (err) {
                        showMessage('复制失败，请手动复制', 'error');
                    }
            
                    document.body.removeChild(textArea);
                }
            }
            
            const selectBtn = () => {
                if(!is_m.value) return;
                const boxElement = document.querySelector('.listBox .screen .fixed .box');
                // 切换 display 属性，显示或隐藏
                if (boxElement.style.display === 'none' || boxElement.style.display === '') {
                    boxElement.style.display = 'block'; // 显示
                } else {
                    boxElement.style.display = ''; // 隐藏
                }
            }
            
            const handleDeviceType = () => {
                const isMobile = window.matchMedia('(max-width: 768px)').matches;
                if (isMobile) {
                    // 手机端的逻辑
                    is_m.value = 1
                    pc_type.value = 1
                } else {
                    // 电脑端的逻辑
                    is_m.value = 0
                    pc_type.value = parseInt(window.__UF__.pcType) || 0;
                }
            };


            // 组件挂载时添加滚动监听
            onMounted(() => {
                handleDeviceType();
                initTheme(); // 初始化主题

                // 从URL参数读取搜索关键词并自动搜索
                const urlParams = new URLSearchParams(window.location.search);
                const wd = urlParams.get('wd');
                if (wd) {
                    keyword.value = decodeURIComponent(wd);
                    // 延迟执行搜索，确保页面完全加载
                    setTimeout(() => {
                        searchBtn();
                    }, 100);
                }

                window.addEventListener('scroll', handleScroll);
                window.addEventListener('resize', handleDeviceType);
                document.addEventListener('click', closeThemeDropdown);
            });

            // 组件卸载时移除滚动监听
            onUnmounted(() => {
                window.removeEventListener('scroll', handleScroll);
                window.removeEventListener('resize', handleDeviceType);
                document.removeEventListener('click', closeThemeDropdown);
            });

            // 暴露一个确定可靠的搜索入口与 keyword ref 到全局，
            // 供 Search.cshtml 等旧脚本不依赖 __vue_app__._instance.setupState 探测也能直接调用
            window.__yfsSearchBtn = searchBtn;
            window.__yfsKeyword = keyword;
            // 同步暴露消息提示，供 Search.cshtml / Home/Index.cshtml 的内联脚本复用（否则 window.showMessage 不存在，提示不生效）
            window.showMessage = showMessage;

            // 抽屉菜单由 Vue 渲染，打开后再刷新一次导航 active，保证同菜单只有一个 active
            function navActiveRefresh() {
                if (typeof window.__yfsSetNavActive === 'function') {
                    window.__yfsSetNavActive();
                }
            }

            // 返回数据和方法
            return { elementOpacity, scrollThreshold, keyword, searchBtn, searchOnly, rankList,newList, setnum, layerVisible, content, saveBtn, rankDj,goLink,changeBtn,copyText,copyToClipboard,drawer,selectBtn,is_m,QList,QLoading,total_result,currentSource,dialogUrl,dialogLoading,dialogItem,is_type,pc_type,panTypeMap,getResource,showResourceDialog,showDetail, downloadWithThunder, currentTheme, showThemeDropdown, switchTheme, themeSwitcherRef, toggleThemeDropdown, navActiveRefresh, dialogPanNameHtml };
        }
    })
    .use(ElementPlus)
    .mount('#app');
    document.documentElement.removeAttribute('data-page-loading');

    // 动态设置导航 active 状态（兼容后台自定义链接，纯原生 JS，所有页面通用）
    // 规则：同一菜单内只保留一个 active。多个 href 都能匹配时取"最长路径"的那一个，
    // 避免 /UserFrontend 作为前缀导致 /UserFrontend/Home/Category 页面上"搜索"与"资源分类"同时点亮。
    (function() {
        // 规范化路径：去掉查询串/锚点/尾部斜杠并统一小写
        function normalizePath(path) {
            var p = (path || '').split('?')[0].split('#')[0].replace(/\s+/g, '');
            if (!p) { return ''; }
            if (p.length > 1) { p = p.replace(/\/+$/, ''); }
            return p.toLowerCase();
        }

        // 路由别名归一化：短链与菜单里的规范地址不同，不归一这些页面会一个 active 都没有
        function canonicalPath(path) {
            var p = normalizePath(path);
            if (p === '/') { return '/userfrontend'; }
            if (p === '/category') { return '/userfrontend/home/category'; }
            // 首页与搜索页统一归到 /userfrontend：后台配置里"搜索"有的写 / 、有的写 /UserFrontend/Home/Search，
            // 归一后两种写法的 active 表现一致（与服务端 _Header.cshtml 的 activeHref 规则相同）
            if (p === '/uf' || p === '/userfrontend/home' || p === '/userfrontend/home/index'
                || p === '/userfrontend/home/search') { return '/userfrontend'; }
            if (p.indexOf('/s/') === 0) { return '/userfrontend'; }
            return p;
        }

        // 在一个菜单容器内做"最长前缀匹配"，命中最长路径的链接加 active，其余一律移除
        function markActive(menuSelector) {
            var links = Array.prototype.slice.call(document.querySelectorAll(menuSelector + ' a[href]'));
            if (links.length === 0) { return; }
            var currentPath = canonicalPath(window.location.pathname);
            var bestLink = null;
            var bestLength = -1;

            links.forEach(function(link) {
                // 链接侧同样要做路由别名归一：后台配置常写短链 /category，
                // 而实际地址是 /UserFrontend/Home/Category，不归一就永远匹配不上
                var href = canonicalPath(link.getAttribute('href'));
                // 忽略空链接、锚点链接和外站链接
                if (!href || href.charAt(0) === '#' || href.indexOf('http') === 0) {
                    return;
                }
                // 完全相等，或按路径段前缀匹配（/userfrontend 不会匹配 /userfrontendx）
                var matched = currentPath === href
                    || (href !== '/' && currentPath.indexOf(href + '/') === 0);
                if (matched && href.length > bestLength) {
                    bestLink = link;
                    bestLength = href.length;
                }
            });

            links.forEach(function(link) {
                if (link === bestLink) {
                    link.classList.add('active');
                } else {
                    link.classList.remove('active');
                }
            });
        }

        function setNavActive() {
            // 顶部导航
            markActive('.top-nav-menu');
            // 手机端抽屉导航
            markActive('.nav-drawer-menu');
            // 手机端底部导航栏
            markActive('.uf-bottom-nav');
        }

        // 暴露给 Vue 内部调用：抽屉菜单由 Vue 渲染，首屏 DOMContentLoaded 时还不存在
        window.__yfsSetNavActive = setNavActive;

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', setNavActive);
        } else {
            setNavActive();
        }
    })();
})();
