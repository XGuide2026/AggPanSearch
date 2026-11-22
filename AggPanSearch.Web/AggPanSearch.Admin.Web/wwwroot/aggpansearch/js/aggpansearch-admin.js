/**
 * 后台外壳脚本：左侧菜单、折叠、顶部栏与整页跳转
 * 说明：菜单点击一律整页跳转，不再使用 iframe + 页签
 */
$(function () {

    // 菜单初始化
    $('#side-menu').metisMenu();

    // 默认展开所有一级菜单（只展开到二级，三级保持收起）
    $('#side-menu > li').each(function () {
        var $li = $(this);
        var $ul = $li.children('ul');
        if ($ul.length === 0) {
            return;
        }
        $ul.addClass('in');
        $li.addClass('menu-open');
    });

    // 固定菜单栏
    $('.sidebar-collapse').slimScroll({
        height: '100%',
        railOpacity: 0.9,
        size: '4px'
    });

    // 展开当前页面所在的菜单，metisMenu 初始化会重置折叠状态，这里再处理一次
    $('#side-menu li.active').parents('li').addClass('active');
    $('#side-menu li.active').parents('ul').addClass('in');
    $('#side-menu li').each(function () {
        if ($(this).children('ul').children('li.active').length > 0) {
            $(this).addClass('active').children('ul').addClass('in');
        }
    });

    // metisMenu 点击折叠项（href="#"）时会切换 li 的 active 高亮，导致未跳转的菜单被点亮、原页面菜单失去高亮。
    // 这里去掉它的点击处理，改为只切换展开状态：高亮始终表示“当前所在页面”。
    $('#side-menu a').off('click.metisMenu');
    $('#side-menu li > a').on('click', function (e) {
        var $li = $(this).parent('li');
        var $ul = $li.children('ul');
        if ($ul.length === 0) {
            // 普通菜单项，正常跳转
            return;
        }
        var href = $(this).attr('href');
        if (href && href !== '#') {
            // 指向真实地址的菜单项，正常跳转
            return;
        }
        e.preventDefault();
        var isOpen = $ul.hasClass('in');
        // 只切换当前菜单的展开状态，不影响其他菜单项
        if (isOpen) {
            $ul.collapse('hide');
            $li.removeClass('menu-open');
        } else {
            $ul.collapse('show');
            $li.addClass('menu-open');
        }
    });

    // 菜单折叠
    $('.navbar-minimalize').on('click', function () {
        $('body').toggleClass('mini-navbar');
        SmoothlyMenu();
    });

    $('#side-menu>li').on('click', function () {
        if ($('body').hasClass('mini-navbar')) {
            NavToggle();
        }
    });
    $('#side-menu>li li a').on('click', function () {
        if ($(window).width() < 769) {
            NavToggle();
        }
    });

    $('.nav-close').on('click', NavToggle);

    // ios浏览器兼容性处理
    if (/(iPhone|iPad|iPod|iOS)/i.test(navigator.userAgent)) {
        $('.admin-main').css('overflow-y', 'auto');
    }

    // 全屏显示
    $('#fullScreen').on('click', function () {
        $('#wrapper').fullScreen();
    });

    // 开发模式 / 管理模式切换：写入 Cookie 后回到首页（Home/Index），由后端按模式输出左侧菜单
    $('#menuModeSwitch').on('click', function () {
        var mode = $(this).attr('data-mode');
        if (!mode) {
            return;
        }
        document.cookie = 'MenuMode=' + mode + '; path=/; max-age=' + (30 * 24 * 60 * 60);
        window.location.href = ctx + 'Home/Index';
    });

    // 按钮权限校验，整页模式下权限数据是异步加载的，未就绪时稍后重试
    applyButtonAuthority(0);
});

function applyButtonAuthority(retry) {
    if (typeof window.getButtonAuthority !== 'function') {
        return;
    }
    var buttonList = [];
    $('#toolbar').find('a').each(function () {
        buttonList.push(this.id);
    });
    $('.toolbar').find('a').each(function () {
        buttonList.push(this.id);
    });
    if (buttonList.length == 0) {
        return;
    }
    var removeButtonList = window.getButtonAuthority(window.location.href, buttonList);
    if (removeButtonList == null) {
        // 权限数据还没返回，最多重试 10 次（约 3 秒）
        if (retry < 10) {
            setTimeout(function () {
                applyButtonAuthority(retry + 1);
            }, 300);
        }
        return;
    }
    $.each(removeButtonList, function (i, val) {
        $("#" + val).remove();
    });
}

$(window).bind("load resize", function () {
    if ($(this).width() < 769) {
        $('body').addClass('mini-navbar');
        $('.navbar-static-side').fadeIn();
    }
});

function NavToggle() {
    $('.navbar-minimalize').trigger('click');
}

function SmoothlyMenu() {
    if (!$('body').hasClass('mini-navbar')) {
        $('#side-menu').hide();
        setTimeout(function () {
            $('#side-menu').fadeIn(500);
        }, 100);
    } else if ($('body').hasClass('fixed-sidebar')) {
        $('#side-menu').hide();
        setTimeout(function () {
            $('#side-menu').fadeIn(500);
        }, 300);
    } else {
        $('#side-menu').removeAttr('style');
    }
}

/**
 * 页面内部跳转（原先用于在框架页中新开页签），这里改为整页跳转
 */
function createMenuItem(dataUrl, menuName) {
    if (dataUrl == undefined || $.trim(dataUrl).length == 0) {
        return false;
    }
    location.href = dataUrl;
    return false;
}

function showUserDetailForm() {
    ys.openDialog({
        title: "个人中心",
        content: ctx + 'OrganizationManage/User/UserDetail' + '?id=' + adminUserId,
        btn: null,
        maxmin: false
    });
}

function showChangePasswordForm() {
    ys.openDialog({
        title: "修改密码",
        content: ctx + 'OrganizationManage/User/ChangePassword' + '?id=' + adminUserId,
        height: "500px",
        callback: function (index, layero) {
            var iframeWin = window[layero.find('iframe')[0]['name']];
            iframeWin.saveForm(index);
        }
    });
}

function showSwitchSkinForm() {
    ys.openDialog({
        title: "切换主题",
        content: ctx + 'Home/Skin',
        width: '530px',
        height: '390px',
        btn: null,
        maxmin: false
    });
}

function loginOff() {
    ys.ajax({
        url: ctx + 'Home/LoginOffJson',
        type: "post",
        success: function (obj) {
            if (obj.Tag == 1) {
                location.href = ctx + 'Home/Index';
            }
            else {
                ys.msgError(obj.Message);
            }
        }
    });
}
