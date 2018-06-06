FROM mono:5.12

COPY RawRead/bin/Debug/RawRead.exe /usr/local/bin/

CMD ["sleep", "inf"]