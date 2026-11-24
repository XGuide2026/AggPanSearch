# -*- coding: utf-8 -*-
import re
p = r'e:\Quark\AggPanSearch\AggPanSearch.Web\AggPanSearch.Admin.Web\wwwroot\aggpansearch\css\aggpansearch-card-list.min.css'
c = open(p, encoding='utf-8').read()
print('len:', len(c))
for key in ['td:first-child', 'td:first-child{', 'input[type="checkbox"]', 'checkbox"]{', '30px', '20px', '26px', '16px', 'btSelectItem']:
    print(key, '->', [m.start() for m in re.finditer(re.escape(key), c)][:5])
idx = c.find('td:first')
print('--- snippet around td:first ---')
print(c[max(0, idx - 80): idx + 500])
idx2 = c.find('checkbox')
print('--- snippet around checkbox ---')
print(c[max(0, idx2 - 80): idx2 + 300])