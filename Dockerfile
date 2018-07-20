FROM mono:5.12

COPY RawRead.exe /usr/local/bin/
COPY *.dll /usr/local/bin/

CMD ["sleep", "inf"]
