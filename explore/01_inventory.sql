SET NOCOUNT ON;
USE Holoo1;
GO
PRINT '=== JSON/ENCODING TEST ===';
SELECT N'تست فارسی' AS T;
GO
PRINT '=== VIEWS (all) ===';
SELECT name FROM sys.views ORDER BY name;
GO
PRINT '=== INVOICE-LIKE TABLES with row counts ===';
SELECT t.name AS TableName,
       SUM(p.rows) AS [Rows],
       MAX(CASE WHEN c.name = 'Fac_Date'    THEN 1 ELSE 0 END) AS HasFacDate,
       MAX(CASE WHEN c.name = 'Fac_Time'    THEN 1 ELSE 0 END) AS HasFacTime,
       MAX(CASE WHEN c.name = 'Fac_Type'    THEN 1 ELSE 0 END) AS HasFacType,
       MAX(CASE WHEN c.name = 'Sum_Price'   THEN 1 ELSE 0 END) AS HasSumPrice,
       MAX(CASE WHEN c.name = 'Takhfif'     THEN 1 ELSE 0 END) AS HasTakhfif,
       MAX(CASE WHEN c.name = 'Delete'      THEN 1 ELSE 0 END) AS HasDelete
FROM sys.tables t
JOIN sys.indexes i      ON i.object_id = t.object_id AND i.index_id IN (0,1)
JOIN sys.partitions p   ON p.object_id = t.object_id AND p.index_id = i.index_id
LEFT JOIN sys.columns c ON c.object_id = t.object_id
GROUP BY t.name
HAVING MAX(CASE WHEN c.name IN ('Fac_Date','F_Date','VCF_Date','AB_Date') THEN 1 ELSE 0 END) = 1
   AND SUM(p.rows) > 0
ORDER BY [Rows] DESC;
GO
