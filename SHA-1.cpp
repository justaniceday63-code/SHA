#include <bits/stdc++.h>
#define N 100000005
using namespace std;
string input, nhiphan;
string block[N];
uint32_t H[6],A,B,C,D,E,k; // mảng 5 phần tử 32 bit để chứa 5 thanh ghi
void generate(){ // khởi tạo 5 thanh ghi
    H[1] = 0x67452301;
    H[2] = 0xEFCDAB89;
    H[3] = 0x98BADCFE;
    H[4] = 0x10325476;
    H[5] = 0xC3D2E1F0;
    
}
void status(){ // 5 thanh ghi A,b,C,D,E. Lưu trạng thái ban đầu của các thanh ghi H)
    A=H[1];
    B=H[2];
    C=H[3];
    D=H[4];
    E=H[5];
}
uint32_t rtlx(uint32_t temp, int x){ // dịch trái x đơn vị
    return (temp<<x)|(temp>>(32-x));
}
void bit_nhi_phan(){  // chuyển sang 8 bit nhị phân 
     for (char c: input) {
        nhiphan += bitset<8>(c).to_string(); 
    }
}
void filling(){
    uint64_t temp = nhiphan.size(); // SHA1 yêu cầu phải được biểu diễn bằng 64 bit nên dùng hàm uint64_t
    nhiphan += '1';
    while(nhiphan.size()%512!=448){
        nhiphan += '0';
    }
    nhiphan += bitset<64>(temp).to_string();
}
void divide(){ //chia các khối 512 bit
    int i=1;
    while (nhiphan.size()/512!=0){
        block[i] += nhiphan.substr(0,512);
        nhiphan.erase(0,512);
        i++;
    }
}
uint32_t decide (uint32_t b, uint32_t c, uint32_t d, int x){
    if (x>=1 && x<=20){
        k= 0x5A827999;
        return (b&c) ^ (~b&d);
    }
    if(x>=21 && x<=40 || x>=61 && x<=80){
        if (x>=21 && x<=40) k = 0x6ED9EBA1;
        if (x>=61 && x<=80) k = 0xCA62C1D6;
        return b^c^d;
    }
    if(x>=41 && x<=60){
        k = 0x8F1BBCDC;
        return (b&c)^(b&d)^(c&d);
    }
    return 0;
}
void hashing(){ // chia thành 16 khối 32 bit và mở rộng lên 80 khối 32 bit, đồng thời thực thi 80 phép tính
    status();
    int i=1;
    while (i<N && block[i]!=""){
        uint32_t W[81];
        for(int j=1;j<=16;j++){
            string temp = block[i].substr((j - 1) * 32, 32); // chia thành các khối 32 bit
            W[j] = bitset<32>(temp).to_ullong();
        }
        for (int j=17; j<=80;j++){ // Mở rộng lên 80 khối 32 bit 
            W[j] = W[j-3]^W[j-8]^W[j-14]^W[j-16];
            W[j] = rtlx(W[j],1);
        }
        for(int t=1;t<=80;t++){
            uint32_t temp = rtlx(A,5) + decide(B,C,D,t) + E + k + W[t];  // tất cả sẽ được tự mod theo 2^32 do dùng hàm 32 bit cho các biến
            E = D;
            D = C;
            C = rtlx(B,30);
            B = A;
            A = temp;
        }
        H[1] += A;
        H[2] += B;
        H[3] += C;
        H[4] += D;
        H[5] += E;
        i++;
    }
}
int main() {
    for (int i=0; i<N; i++){
        block[i]= "";
    }
    generate(); // hàm khởi tạo 5 thanh ghi
    cin >> input; // nhập
    bit_nhi_phan(); // hàm chuyển sang 8 bit nhị phân
    filling(); // hàm fill bit 1,0 và các byte đầu vào
    divide(); // hàm chia block 512 bit
    hashing(); // hàm tính toán chính
    cout << hex << nouppercase << setfill('0');
    for(int i=1; i<=5; i++){
        cout << setw(8) << H[i];
    }
    return 0;
}
