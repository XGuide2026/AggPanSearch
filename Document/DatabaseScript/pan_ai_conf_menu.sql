-- =============================================================================
-- AI设置独立页面 菜单脚本（MySQL）
-- -----------------------------------------------------------------------------
-- 用途：在「网盘管理」目录下新增「AI设置」页面菜单，及其 3 个按钮权限
--       （pan:panaiconf:view / search / edit），对应新增的代码页面：
--       Areas/PanManage/Controllers/PanAiConfController.cs
--       Areas/PanManage/Views/PanAiConf/PanAiConfIndex.cshtml
-- 幂等：按 MenuUrl / ParentId+Authorize 判重，重复执行不会产生重复菜单。
-- 注意：
--   1) 菜单父级「跟随搜索设置菜单」：以 MenuUrl='PanManage/PanSearchConf/PanSearchConfIndex'
--      一条记录的 ParentId 作为本菜单父级，避免硬编码父级 ID 导致环境不符。
--      若库里还没有该行（说明搜索设置独立页也未部署），脚本会跳过插入，先部署搜索设置菜单。
--   2) 排序列 MenuSort 取「同级最大序号 + 1」，放在同一目录的最后，可自行在菜单管理里调整。
--   3) 用户菜单可见性：超级管理员（SysUser.IsSystem=1）登录后左侧菜单立即可见；
--      普通角色还需在「系统管理 - 菜单管理」中为该菜单勾选角色授权（SysMenuAuthorize）。
--   4) 新菜单 Id 用毫秒时间戳拼接序号生成（15 位左右），避开雪花 ID 位数冲突；
--      已存在菜单时判重跳过，不会覆盖原记录。
-- =============================================================================

-- 0) 定位父级：跟随「搜索设置」（PanSearchConf）菜单的父级
SET @parentId = NULL;
SELECT ParentId INTO @parentId
FROM SysMenu
WHERE MenuUrl = 'PanManage/PanSearchConf/PanSearchConfIndex'
  AND BaseIsDelete = 0
LIMIT 1;

-- 新菜单 Id：毫秒时间戳拼接序号，保证多次脚本执行生成的 Id 不冲突
SET @m := UNIX_TIMESTAMP(NOW(3)) * 1000;
SET @menuId   := CAST(CONCAT(@m, '0') AS UNSIGNED);
SET @btnId1   := CAST(CONCAT(@m, '1') AS UNSIGNED); -- pan:panaiconf:view
SET @btnId2   := CAST(CONCAT(@m, '2') AS UNSIGNED); -- pan:panaiconf:search
SET @btnId3   := CAST(CONCAT(@m, '3') AS UNSIGNED); -- pan:panaiconf:edit

-- 1) 插入「AI设置」页面菜单（MenuType=2；无父级或已存在则跳过）
INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT @menuId, @parentId, 'AI设置', 'fa fa-microchip', 'PanManage/PanAiConf/PanAiConfIndex', '',
       t.MaxSort + 1, 2, 1, 'pan:panaiconf:view', 'AI 设置独立页面（原「基础设置 - AI设置」分类）',
       0, NOW(), 1, NOW(), 1, 0
FROM (
    SELECT IFNULL(MAX(MenuSort), 0) AS MaxSort
    FROM SysMenu
    WHERE ParentId <=> @parentId AND BaseIsDelete = 0
) t
WHERE @parentId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE MenuUrl = 'PanManage/PanAiConf/PanAiConfIndex' AND BaseIsDelete = 0);

-- 2) 读取已存在的「AI设置」菜单 Id（插入被跳过时也能拿到）
SET @menuId = NULL;
SELECT Id INTO @menuId
FROM SysMenu
WHERE MenuUrl = 'PanManage/PanAiConf/PanAiConfIndex' AND BaseIsDelete = 0
LIMIT 1;

-- 3) 插入 3 条按钮权限（MenuType=3），按 ParentId + Authorize 判重
INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT @btnId1, @menuId, '页面访问', 'fa fa-eye', '', '',
       1, 3, 1, 'pan:panaiconf:view', 'AI设置：访问页面',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE @menuId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = @menuId AND Authorize = 'pan:panaiconf:view' AND BaseIsDelete = 0);

INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT @btnId2, @menuId, '查询配置', 'fa fa-search', '', '',
       2, 3, 1, 'pan:panaiconf:search', 'AI设置：加载配置列表',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE @menuId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = @menuId AND Authorize = 'pan:panaiconf:search' AND BaseIsDelete = 0);

INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT @btnId3, @menuId, '编辑保存', 'fa fa-edit', '', '',
       3, 3, 1, 'pan:panaiconf:edit', 'AI设置：保存配置',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE @menuId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = @menuId AND Authorize = 'pan:panaiconf:edit' AND BaseIsDelete = 0);

-- 4) 提交事务
COMMIT;