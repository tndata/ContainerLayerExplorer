FROM busybox:1.38.0-glibc

# Layer 2: a working directory. Everything below goes into /src.
WORKDIR /src

# Layer 3: two new files (A = added).
COPY Hello1.txt Hello2.txt ./

# Layer 4: Hello3.txt is copied over Hello1.txt (M = changed). The whole file is stored again.
COPY Hello3.txt Hello1.txt

# Layer 5: Hello2.txt is deleted (D = deleted). It is only hidden: its data is still stored in layer 3.
RUN rm Hello2.txt
