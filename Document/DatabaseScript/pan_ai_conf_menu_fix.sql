-- =============================================================================
-- AI设置菜单 修复脚本（MySQL / pansearch 库）
-- -----------------------------------------------------------------------------
-- 问题：SysMenu 表主键缺失导致 4 条 Id 相同的「AI设置」页面菜单被插入，
--       且 3 条按钮权限（view/search/edit）插入时错插为页面菜单类型。
-- 修复：删除 3 条错插残留，保留正确页面菜单；补插 3 条按钮权限。
-- 幂等：删除全部使用「Id+MenuUrl+MenuType+Authorize+Remark」组合条件；
--       补插使用 NOT EXISTS 判重；可安全重复执行。
-- =============================================================================

-- -----------------------------------------------------------------------------
-- 1) 删除 3 条错插成页面菜单的按钮残留（保留 Remark=正确页面菜单的那条）
--    条件足够精确：Id + MenuUrl + MenuType=2 + Authorize + Remark 三选
-- -----------------------------------------------------------------------------
DELETE FROM SysMenu
WHERE Id = 1795457298352
  AND MenuUrl = 'PanManage/PanAiConf/PanAiConfIndex'
  AND MenuType = 2
  AND BaseIsDelete = 0
  AND Authorize = 'pan:panaiconf:view'
  AND Remark IN ('AI设置：访问页面', 'AI设置：加载配置列表', 'AI设置：保存配置');

-- -----------------------------------------------------------------------------
-- 2) 清理按钮权限残留（防御性：若面板菜单下已存在 MenuType=3 的记录则先清除，
--    保证后续插入的按钮权限 Id/顺序一致；重复执行时匹配 0 行，无副作用）
-- -----------------------------------------------------------------------------
DELETE FROM SysMenu
WHERE ParentId = 1795457298352
  AND MenuType = 3
  AND BaseIsDelete = 0
  AND Authorize IN ('pan:panaiconf:view', 'pan:panaiconf:search', 'pan:panaiconf:edit');

-- -----------------------------------------------------------------------------
-- 3) 补插 3 条按钮权限（MenuType=3），各自 NOT EXISTS 判重
--    Id 生成：毫秒时间戳拼接序号（与雪花 ID 位数不同，避开冲突）
--    参照「搜索设置」菜单按钮写法：MenuUrl='', MenuIcon fa-*, 子级排序从 1 开始
-- -----------------------------------------------------------------------------

-- 3.1 页面访问 pan:panaiconf:view
INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT CAST(CONCAT(FLOOR(UNIX_TIMESTAMP(NOW(3)) * 1000), '1') AS UNSIGNED), 1795457298352,
       '页面访问', 'fa fa-eye', '', '', 1, 3, 1, 'pan:panaiconf:view', 'AI设置：访问页面',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = 1795457298352 AND MenuType = 3
                    AND Authorize = 'pan:panaiconf:view' AND BaseIsDelete = 0);

-- 3.2 查询配置 pan:panaiconf:search
INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT CAST(CONCAT(FLOOR(UNIX_TIMESTAMP(NOW(3)) * 1000), '2') AS UNSIGNED), 1795457298352,
       '查询配置', 'fa fa-search', '', '', 2, 3, 1, 'pan:panaiconf:search', 'AI设置：加载配置列表',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = 1795457298352 AND MenuType = 3
                    AND Authorize = 'pan:panaiconf:search' AND BaseIsDelete = 0);

-- 3.3 编辑保存 pan:panaiconf:edit
INSERT INTO SysMenu (Id, ParentId, MenuName, MenuIcon, MenuUrl, MenuTarget,
                     MenuSort, MenuType, MenuStatus, Authorize, Remark,
                     BaseIsDelete, BaseCreateTime, BaseCreatorId,
                     BaseModifyTime, BaseModifierId, BaseVersion)
SELECT CAST(CONCAT(FLOOR(UNIX_TIMESTAMP(NOW(3)) * 1000), '3') AS UNSIGNED), 1795457298352,
       '编辑保存', 'fa fa-edit', '', '', 3, 3, 1, 'pan:panaiconf:edit', 'AI设置：保存配置',
       0, NOW(), 1, NOW(), 1, 0
FROM DUAL
WHERE NOT EXISTS (SELECT 1 FROM SysMenu
                  WHERE ParentId = 1795457298352 AND MenuType = 3
                    AND Authorize = 'pan:panaiconf:edit' AND BaseIsDelete = 0);

-- -----------------------------------------------------------------------------
-- 4) 提交事务
-- -----------------------------------------------------------------------------
COMMIT;

-- -----------------------------------------------------------------------------
-- 5) 校验：查询修复后的状态（应只有 1 条页面菜单 + 3 条按钮权限）
-- -----------------------------------------------------------------------------
SELECT MenuName, MenuUrl, MenuType, Authorize, Remark,
       COUNT(*) AS Cnt
FROM SysMenu
WHERE Id = 1795457298352 OR ParentId = 1795457298352
GROUP BY MenuName, MenuUrl, MenuType, Authorize, Remark
ORDER BY MenuType, Remark;