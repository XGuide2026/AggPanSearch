-- ============================================================================
-- 文件大小字段 FileSize 改为 int（单位 M）迁移脚本
-- 适用库：pansearch（MySQL）
-- 说明：
--   1. 兼容旧数据：历史 FileSize 存的是原文文本（如 '6.2G'、'820K'、'1.2TB'），
--      无法直接转 int，故先一律置 NULL（仅新采集/分析数据写入 M 数值）。
--   2. 1000/1024 进制说明：采集端按 1024 进制换算为 M 后入库（如 6.2G -> 6349M）。
--   3. 脚本幂等：先置空再 MODIFY，避免文本转 int 报错；可重复执行。
-- 执行建议：先备份两表再执行。
-- ============================================================================

-- 1. 采集资源表 pan_source_collect：置空历史文本值
UPDATE pan_source_collect SET FileSize = NULL WHERE FileSize IS NOT NULL;

-- 2. 采集资源表 pan_source_collect：字段改为 int（单位 M），可空
ALTER TABLE pan_source_collect MODIFY COLUMN FileSize int NULL COMMENT '文件大小（单位 M）';

-- 3. 本地资源表 pan_source：置空历史文本值
UPDATE pan_source SET FileSize = NULL WHERE FileSize IS NOT NULL;

-- 4. 本地资源表 pan_source：字段改为 int（单位 M），可空
ALTER TABLE pan_source MODIFY COLUMN FileSize int NULL COMMENT '文件大小（单位 M）';