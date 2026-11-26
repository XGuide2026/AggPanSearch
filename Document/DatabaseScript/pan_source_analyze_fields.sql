-- =============================================================================
-- 资源信息分析 三个新字段 建表脚本（MySQL / pansearch 库）
-- -----------------------------------------------------------------------------
-- 背景：采集资源的图片下载完成后，「资源信息分析」打开网盘分享页解析
--       span.info-stat（如「共 112 个文件 6.2G」「永久有效」），采集以下 3 个字段：
--         FileCount            文件个数      int
--         FileSize             文件大小      varchar(50)  存网页原文（如 6.2G）
--         ShareValidityPeriod  资源有效期    varchar(100) 存网页原文（如 永久有效）
-- 归属表：
--       pan_source_collect（采集资源表）：分析结果直接写入
--       pan_source        （本地资源表）：转存时从采集表拷贝
-- 幂等：每列均通过 information_schema.COLUMNS 判断是否已存在，存在则跳过；
--       可安全重复执行。
-- =============================================================================

-- =============================================================================
-- 1) 采集资源表 pan_source_collect
-- =============================================================================
SET @db   := DATABASE();
SET @tbl1 := 'pan_source_collect';
SET @tbl2 := 'pan_source';

-- 1.1 FileCount（文件个数）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl1 AND COLUMN_NAME = 'FileCount') = 0,
    'ALTER TABLE `pan_source_collect` ADD COLUMN `FileCount` int NULL COMMENT ''文件个数''',
    'SELECT ''pan_source_collect.FileCount already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- 1.2 FileSize（文件大小，网页原文）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl1 AND COLUMN_NAME = 'FileSize') = 0,
    'ALTER TABLE `pan_source_collect` ADD COLUMN `FileSize` varchar(50) NULL COMMENT ''文件大小''',
    'SELECT ''pan_source_collect.FileSize already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- 1.3 ShareValidityPeriod（资源有效期，网页原文）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl1 AND COLUMN_NAME = 'ShareValidityPeriod') = 0,
    'ALTER TABLE `pan_source_collect` ADD COLUMN `ShareValidityPeriod` varchar(100) NULL COMMENT ''资源有效期''',
    'SELECT ''pan_source_collect.ShareValidityPeriod already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- =============================================================================
-- 2) 本地资源表 pan_source（默认库名可能不同，注意替换 @db）
-- =============================================================================

-- 2.1 FileCount（文件个数）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl2 AND COLUMN_NAME = 'FileCount') = 0,
    'ALTER TABLE `pan_source` ADD COLUMN `FileCount` int NULL COMMENT ''文件个数''',
    'SELECT ''pan_source.FileCount already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- 2.2 FileSize（文件大小，网页原文）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl2 AND COLUMN_NAME = 'FileSize') = 0,
    'ALTER TABLE `pan_source` ADD COLUMN `FileSize` varchar(50) NULL COMMENT ''文件大小''',
    'SELECT ''pan_source.FileSize already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- 2.3 ShareValidityPeriod（资源有效期，网页原文）
SET @sql := IF(
    (SELECT COUNT(*) FROM information_schema.COLUMNS
      WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @tbl2 AND COLUMN_NAME = 'ShareValidityPeriod') = 0,
    'ALTER TABLE `pan_source` ADD COLUMN `ShareValidityPeriod` varchar(100) NULL COMMENT ''资源有效期''',
    'SELECT ''pan_source.ShareValidityPeriod already exists, skipped''');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

-- =============================================================================
-- 3) 校验：应能看到两表各 3 列
-- =============================================================================
SELECT TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, COLUMN_COMMENT
FROM information_schema.COLUMNS
WHERE TABLE_SCHEMA = @db
  AND TABLE_NAME IN ('pan_source_collect', 'pan_source')
  AND COLUMN_NAME IN ('FileCount', 'FileSize', 'ShareValidityPeriod')
ORDER BY TABLE_NAME, ORDINAL_POSITION;